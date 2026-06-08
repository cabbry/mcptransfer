// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

/// @title  KeyRegistry
/// @notice Each Ethereum address publishes BOTH its secp256k1 compressed
///         public key AND its ML-KEM-768 public key (FIPS 203). Both are
///         required: the sender uses ECDH on secp256k1 plus ML-KEM
///         encapsulation in parallel to derive an AES key (hybrid PQC KEM).
/// @dev    Strictly self-service: only msg.sender can write its own entry.
///         No admin, no upgradeability, no fee. Re-publishing overwrites.
contract KeyRegistry {
    /// @notice Length in bytes of a secp256k1 compressed public key.
    uint256 public constant SECP256K1_COMPRESSED_LENGTH = 33;

    /// @notice Length in bytes of an FIPS 203 ML-KEM-768 public key.
    uint256 public constant ML_KEM_768_PUBKEY_LENGTH = 1184;

    /// @notice Current schema version of the published pubkey blob.
    uint64 public constant KEY_VERSION = 1;

    /// @notice Emitted on every successful publish (initial or rotation).
    /// @param who              The address that published; same as msg.sender.
    /// @param secp256k1Pubkey  33-byte compressed secp256k1 public key.
    /// @param mlkemPubkey      1184-byte ML-KEM-768 public key blob.
    /// @param version          Schema version; bumped if we ever encode differently.
    event KeysPublished(
        address indexed who,
        bytes secp256k1Pubkey,
        bytes mlkemPubkey,
        uint64 version
    );

    mapping(address => bytes) private _secp256k1Pubkey;
    mapping(address => bytes) private _mlkemPubkey;

    /// @notice Publish (or overwrite) msg.sender's two public keys.
    /// @param  secp256k1Pubkey Must be exactly 33 bytes (compressed form).
    /// @param  mlkemPubkey     Must be exactly 1184 bytes (ML-KEM-768).
    /// @dev    The contract does NOT verify that secp256k1Pubkey derives to
    ///         msg.sender. Readers should check this client-side via
    ///         keccak256(uncompressed[1:])[12:] == msg.sender before using
    ///         the key for ECDH.
    function publish(bytes calldata secp256k1Pubkey, bytes calldata mlkemPubkey) external {
        require(
            secp256k1Pubkey.length == SECP256K1_COMPRESSED_LENGTH,
            "KeyRegistry: wrong secp256k1 length"
        );
        require(
            mlkemPubkey.length == ML_KEM_768_PUBKEY_LENGTH,
            "KeyRegistry: wrong mlkem length"
        );
        _secp256k1Pubkey[msg.sender] = secp256k1Pubkey;
        _mlkemPubkey[msg.sender] = mlkemPubkey;
        emit KeysPublished(msg.sender, secp256k1Pubkey, mlkemPubkey, KEY_VERSION);
    }

    /// @notice Return the secp256k1 compressed public key registered for `who`,
    ///         or empty bytes if none has been published.
    function getSecp256k1(address who) external view returns (bytes memory) {
        return _secp256k1Pubkey[who];
    }

    /// @notice Return the ML-KEM-768 public key registered for `who`, or
    ///         empty bytes if none has been published.
    function getMlKem(address who) external view returns (bytes memory) {
        return _mlkemPubkey[who];
    }
}
