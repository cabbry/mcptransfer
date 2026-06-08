using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MCPTransfer.Core.Chain;
using MCPTransfer.Core.Crypto;

namespace MCPTransfer.Tests.Integration;

/// <summary>
/// xUnit collection fixture for the live-chain integration tests. When
/// <em>enabled</em>, it locates a Foundry install, starts a throwaway
/// <c>anvil</c> devnet, deploys the three MCPTransfer contracts with the
/// project's <c>Deploy.s.sol</c> script, and exposes a ready
/// <see cref="ChainConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// These tests are SKIPPED by default — they need a local Foundry toolchain
/// and spend a couple of seconds spinning up a chain, which we do not want in
/// the unit suite or in shared CI. They run only when the environment variable
/// <see cref="GateEnvVar"/> is set to <c>1</c>/<c>true</c>. If the gate is set
/// but Foundry cannot be found, the fixture stays disabled (with a reason) so
/// the tests still no-op rather than failing the run.
/// </para>
/// <para>
/// xUnit v2 has no <c>Assert.Skip</c>, so each test in the collection guards on
/// <see cref="Enabled"/> and returns early when the fixture is off.
/// </para>
/// </remarks>
public sealed class AnvilFixture : IAsyncLifetime
{
    /// <summary>Set to <c>1</c> or <c>true</c> to actually run the anvil tests.</summary>
    public const string GateEnvVar = "MCPTX_RUN_ANVIL_TESTS";

    /// <summary>Optional override for the directory containing anvil/forge.</summary>
    public const string FoundryBinEnvVar = "MCPTX_FOUNDRY_BIN";

    /// <summary>Optional override for the anvil TCP port.</summary>
    public const string PortEnvVar = "MCPTX_ANVIL_PORT";

    private const long ChainId = ChainConfig.AnvilChainId; // 31337

    /// <summary>
    /// Anvil's deterministic dev accounts (default mnemonic
    /// "test test test test test test test test test test test junk").
    /// These are PUBLIC, well-known test keys — safe to commit; they only ever
    /// fund a local throwaway chain and must never be used on a real network.
    /// Index 0 is the deployer; the tests partition 1..9 so no two tests share
    /// an account (an AgentDirectory handle claim is permanent per address).
    /// </summary>
    public static readonly string[] DevPrivateKeys =
    {
        "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80", // 0 deployer
        "0x59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d", // 1
        "0x5de4111afa1a4b94908f83103eb1f1706367c2e68ca870fc3fb9a804cdab365a", // 2
        "0x7c852118294e51e653712a81e05800f419141751be58f605c371e15141b007a6", // 3
        "0x47e179ec197488593b187f80a00eb0da91f1b9d0b13f8733639f19c30a34926a", // 4
        "0x8b3a350cf5c34c9194ca85829a2df0ec3153be0318b5e2d3348e872092edffba", // 5
        "0x92db14e403b83dfe3df233f83dfa3a0d7096f21ca9b0d6d6b8d88b2b4ec1564e", // 6
        "0x4bbbf85ce3377467afe5d46f804f221813b2bb87f24d81f60f1fcdbf7cbf4356", // 7
        "0xdbda1821b80551c9d65939329250298aa3472ba22feea921c0cf5d620ea67b97", // 8
        "0x2a871d0798f97d79848a013d4936a73bf4cc922c825d33c1cf7073dfde6f6a9d", // 9
    };

    public bool Enabled { get; private set; }
    public string? SkipReason { get; private set; }
    public ChainConfig Config { get; private set; } = null!;

    private Process? _anvil;
    private readonly StringBuilder _anvilLog = new();

    public async Task InitializeAsync()
    {
        var gate = Environment.GetEnvironmentVariable(GateEnvVar);
        if (gate is not ("1" or "true" or "TRUE" or "True"))
        {
            SkipReason = $"{GateEnvVar} is not set; anvil integration tests skipped.";
            return;
        }

        var anvilExe = ResolveExe("anvil");
        var forgeExe = ResolveExe("forge");
        if (anvilExe is null || forgeExe is null)
        {
            SkipReason =
                $"Foundry not found (set {FoundryBinEnvVar} to the dir containing anvil/forge, "
                + "or add it to PATH). Anvil integration tests skipped.";
            return;
        }

        var contractsDir = FindContractsDir();
        if (contractsDir is null)
        {
            SkipReason = "Could not locate the contracts/ directory from the test base dir.";
            return;
        }

        var port = ResolvePort();
        var rpcUrl = $"http://127.0.0.1:{port}";

        StartAnvil(anvilExe, port);
        try
        {
            await WaitForRpcAsync(rpcUrl, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var addresses = await DeployAsync(forgeExe, contractsDir, rpcUrl).ConfigureAwait(false);

            Config = new ChainConfig
            {
                RpcUrl = rpcUrl,
                ChainId = ChainId,
                FileRegistryAddress = EthereumAddress.FromHex(addresses["FileRegistry"]),
                KeyRegistryAddress = EthereumAddress.FromHex(addresses["KeyRegistry"]),
                AgentDirectoryAddress = EthereumAddress.FromHex(addresses["AgentDirectory"]),
            };
            Enabled = true;
        }
        catch
        {
            await StopAnvilAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisposeAsync() => await StopAnvilAsync().ConfigureAwait(false);

    /// <summary>Build a full agent identity bound to dev account <paramref name="index"/>.</summary>
    public static AgentIdentity Identity(int index) => AgentIdentity.FromKeys(
        Secp256k1KeyPair.FromPrivateKeyHex(DevPrivateKeys[index]),
        MlKemKeyPair.Generate(),
        MlDsaKeyPair.Generate());

    // ---- internals ---------------------------------------------------------

    private void StartAnvil(string anvilExe, int port)
    {
        var psi = new ProcessStartInfo(anvilExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--chain-id");
        psi.ArgumentList.Add(ChainId.ToString());
        psi.ArgumentList.Add("--silent");

        _anvil = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Drain the pipes so a chatty anvil can never block on a full buffer.
        _anvil.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_anvilLog) _anvilLog.AppendLine(e.Data); };
        _anvil.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_anvilLog) _anvilLog.AppendLine(e.Data); };
        _anvil.Start();
        _anvil.BeginOutputReadLine();
        _anvil.BeginErrorReadLine();
    }

    private async Task StopAnvilAsync()
    {
        if (_anvil is null) return;
        try
        {
            if (!_anvil.HasExited)
                _anvil.Kill(entireProcessTree: true);
            await _anvil.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort teardown.
        }
        finally
        {
            _anvil.Dispose();
            _anvil = null;
        }
    }

    /// <summary>Poll eth_blockNumber until anvil answers (or we time out).</summary>
    private static async Task WaitForRpcAsync(string rpcUrl, TimeSpan timeout)
    {
        var zero = EthereumAddress.FromHex("0x" + new string('0', 40));
        var probeConfig = new ChainConfig
        {
            RpcUrl = rpcUrl,
            ChainId = ChainId,
            FileRegistryAddress = zero,
            KeyRegistryAddress = zero,
            AgentDirectoryAddress = zero,
        };
        var probe = new FileRegistryClient(probeConfig);

        var sw = Stopwatch.StartNew();
        Exception? last = null;
        while (sw.Elapsed < timeout)
        {
            try
            {
                await probe.GetLatestBlockNumberAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        throw new TimeoutException($"anvil did not become ready at {rpcUrl} within {timeout.TotalSeconds:n0}s.", last);
    }

    private async Task<Dictionary<string, string>> DeployAsync(string forgeExe, string contractsDir, string rpcUrl)
    {
        var psi = new ProcessStartInfo(forgeExe)
        {
            WorkingDirectory = contractsDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "script", "script/Deploy.s.sol:Deploy",
            "--rpc-url", rpcUrl,
            "--private-key", DevPrivateKeys[0],
            "--broadcast",
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start `forge script`.");
        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"`forge script` failed (exit {proc.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }

        var runLatest = Path.Combine(
            contractsDir, "broadcast", "Deploy.s.sol", ChainId.ToString(), "run-latest.json");
        if (!File.Exists(runLatest))
            throw new InvalidOperationException($"Broadcast artifact not found at {runLatest}.");

        return ParseDeployedAddresses(await File.ReadAllTextAsync(runLatest).ConfigureAwait(false));
    }

    /// <summary>Map contractName → contractAddress from a forge broadcast file.</summary>
    private static Dictionary<string, string> ParseDeployedAddresses(string runLatestJson)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(runLatestJson);
        foreach (var tx in doc.RootElement.GetProperty("transactions").EnumerateArray())
        {
            if (tx.TryGetProperty("contractName", out var name) && name.ValueKind == JsonValueKind.String
                && tx.TryGetProperty("contractAddress", out var addr) && addr.ValueKind == JsonValueKind.String)
            {
                map[name.GetString()!] = addr.GetString()!;
            }
        }

        foreach (var required in new[] { "FileRegistry", "KeyRegistry", "AgentDirectory" })
        {
            if (!map.ContainsKey(required))
                throw new InvalidOperationException($"Deploy broadcast did not include {required}.");
        }
        return map;
    }

    private static int ResolvePort()
    {
        var raw = Environment.GetEnvironmentVariable(PortEnvVar);
        return int.TryParse(raw, out var p) && p is > 0 and < 65536 ? p : 8559;
    }

    /// <summary>
    /// Resolve a Foundry executable by name. Prefers <see cref="FoundryBinEnvVar"/>,
    /// then the standard <c>~/.foundry/bin</c>, then this machine's local install
    /// at <c>E:\foundry</c>, and finally falls back to a bare name resolved via
    /// PATH. Returns <c>null</c> only if a probed full path was given but missing.
    /// </summary>
    private static string? ResolveExe(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;

        var dirs = new List<string>();
        var envBin = Environment.GetEnvironmentVariable(FoundryBinEnvVar);
        if (!string.IsNullOrWhiteSpace(envBin)) dirs.Add(envBin);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) dirs.Add(Path.Combine(home, ".foundry", "bin"));

        if (OperatingSystem.IsWindows())
            dirs.Add(@"E:\foundry");
        else
            dirs.Add("/usr/local/bin");

        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        // Fall back to PATH resolution (works if Foundry is on PATH).
        return ResolveOnPath(exe);
    }

    private static string? ResolveOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Skip malformed PATH entries.
            }
        }
        return null;
    }

    /// <summary>Walk up from the test base dir until a sibling contracts/foundry.toml is found.</summary>
    private static string? FindContractsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "contracts", "foundry.toml");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "contracts");
            dir = dir.Parent;
        }
        return null;
    }
}

/// <summary>
/// Binds the <see cref="AnvilFixture"/> to the "Anvil" test collection so the
/// chain is started once and shared by every test in it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AnvilCollection : ICollectionFixture<AnvilFixture>
{
    public const string Name = "Anvil";
}
