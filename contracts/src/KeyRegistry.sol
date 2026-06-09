// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

/// @title  KeyRegistry
/// @notice Each Ethereum address publishes its secp256k1 compressed public
///         key (33 bytes, in clear — needed for the ECDH half of the hybrid
///         KEM) plus a COMMITMENT to its ML-KEM-768 public key: the keccak256
///         hash of the 1184-byte key and a content-addressed pointer (CID)
///         where the full key can be fetched.
/// @dev    v2 design — the full ML-KEM key used to live on-chain (~1.3M gas
///         per publish). Storing a 32-byte hash commitment plus a short CID
///         instead:
///           - cuts publish gas by an order of magnitude;
///           - keeps the chain itself free of bulk key material — the key is
///             distributed off-chain (the reference client pins it to IPFS,
///             but ANY channel works: the commitment makes the distribution
///             channel untrusted);
///           - readers MUST verify keccak256(fetchedKey) == mlkemHash before
///             encapsulating to it.
///         Strictly self-service: only msg.sender can write its own entry.
///         No admin, no upgradeability, no fee. Re-publishing overwrites.
contract KeyRegistry {
    /// @notice Length in bytes of a secp256k1 compressed public key.
    uint256 public constant SECP256K1_COMPRESSED_LENGTH = 33;

    /// @notice Length in bytes of the FIPS 203 ML-KEM-768 public key that
    ///         `mlkemHash` commits to (informational; the key itself is not
    ///         stored on-chain).
    uint256 public constant ML_KEM_768_PUBKEY_LENGTH = 1184;

    /// @notice Upper bound on the CID string, generous for CIDv0/v1 and
    ///         hex digests while still bounding storage writes.
    uint256 public constant MAX_CID_LENGTH = 128;

    /// @notice Current schema version of the published entry.
    uint64 public constant KEY_VERSION = 2;

    /// @notice Emitted on every successful publish (initial or rotation).
    /// @param who              The address that published; same as msg.sender.
    /// @param secp256k1Pubkey  33-byte compressed secp256k1 public key.
    /// @param mlkemHash        keccak256 of the 1184-byte ML-KEM-768 public key.
    /// @param mlkemCid         Content-addressed pointer to fetch the full key.
    /// @param version          Schema version; bumped if we ever encode differently.
    event KeysPublished(
        address indexed who,
        bytes secp256k1Pubkey,
        bytes32 mlkemHash,
        string mlkemCid,
        uint64 version
    );

    mapping(address => bytes) private _secp256k1Pubkey;
    mapping(address => bytes32) private _mlkemHash;
    mapping(address => string) private _mlkemCid;

    /// @notice Publish (or overwrite) msg.sender's key entry.
    /// @param  secp256k1Pubkey Must be exactly 33 bytes (compressed form).
    /// @param  mlkemHash       keccak256 of the full ML-KEM-768 public key.
    /// @param  mlkemCid        Non-empty pointer (<= 128 bytes) to the full key.
    /// @dev    The contract does NOT verify that secp256k1Pubkey derives to
    ///         msg.sender, nor that mlkemCid resolves to bytes matching
    ///         mlkemHash. Readers must check both client-side before using
    ///         the keys.
    function publish(
        bytes calldata secp256k1Pubkey,
        bytes32 mlkemHash,
        string calldata mlkemCid
    ) external {
        require(
            secp256k1Pubkey.length == SECP256K1_COMPRESSED_LENGTH,
            "KeyRegistry: wrong secp256k1 length"
        );
        require(mlkemHash != bytes32(0), "KeyRegistry: zero mlkem hash");
        require(bytes(mlkemCid).length != 0, "KeyRegistry: empty mlkem cid");
        require(bytes(mlkemCid).length <= MAX_CID_LENGTH, "KeyRegistry: cid too long");

        _secp256k1Pubkey[msg.sender] = secp256k1Pubkey;
        _mlkemHash[msg.sender] = mlkemHash;
        _mlkemCid[msg.sender] = mlkemCid;
        emit KeysPublished(msg.sender, secp256k1Pubkey, mlkemHash, mlkemCid, KEY_VERSION);
    }

    /// @notice Return the secp256k1 compressed public key registered for `who`,
    ///         or empty bytes if none has been published.
    function getSecp256k1(address who) external view returns (bytes memory) {
        return _secp256k1Pubkey[who];
    }

    /// @notice Return the ML-KEM commitment registered for `who`: the
    ///         keccak256 hash of the key and the pointer to fetch it. Both
    ///         are zero/empty if `who` has never published.
    function getMlKem(address who)
        external
        view
        returns (bytes32 mlkemHash, string memory mlkemCid)
    {
        return (_mlkemHash[who], _mlkemCid[who]);
    }
}
