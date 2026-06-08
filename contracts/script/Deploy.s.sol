// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

import {Script, console2} from "forge-std/Script.sol";
import {FileRegistry} from "../src/FileRegistry.sol";
import {KeyRegistry} from "../src/KeyRegistry.sol";
import {AgentDirectory} from "../src/AgentDirectory.sol";

/// @title  Deploy
/// @notice Deploys the three MCPTransfer contracts to the active chain.
///         Targets:
///           - anvil local: `forge script script/Deploy.s.sol --rpc-url anvil
///             --private-key 0xac... --broadcast`
///           - Polygon Amoy: `forge script script/Deploy.s.sol --rpc-url amoy
///             --private-key $AMOY_DEPLOYER_PK --broadcast --verify`
/// @dev    Uses `vm.startBroadcast()` with no arg so the deployer key comes
///         from the `--private-key` CLI flag (or `PRIVATE_KEY` env var).
contract Deploy is Script {
    function run() external {
        vm.startBroadcast();

        FileRegistry fileRegistry = new FileRegistry();
        KeyRegistry keyRegistry = new KeyRegistry();
        AgentDirectory agentDirectory = new AgentDirectory();

        vm.stopBroadcast();

        console2.log("==========================================================");
        console2.log("MCPTransfer contracts deployed");
        console2.log("Chain ID        :", block.chainid);
        console2.log("FileRegistry    :", address(fileRegistry));
        console2.log("KeyRegistry     :", address(keyRegistry));
        console2.log("AgentDirectory  :", address(agentDirectory));
        console2.log("==========================================================");
    }
}
