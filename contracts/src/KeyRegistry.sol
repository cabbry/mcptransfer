// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

/// @title  KeyRegistry
/// @notice Each Ethereum address can publish its own ML-KEM-768 public key
///         (FIPS 203 / Module-Lattice KEM). Senders look up the recipient's
///         pubkey before encapsulating the AES data key for a transfer.
/// @dev    Strictly self-service: only msg.sender can write its own entry.
///         No admin, no upgradeability, no fee. Re-publishing overwrites.
contract KeyRegistry {
    /// @notice Length in bytes of an FIPS 203 ML-KEM-768 public key.
    uint256 public constant ML_KEM_768_PUBKEY_LENGTH = 1184;

    /// @notice Current schema version of the published pubkey blob.
    uint64 public constant KEY_VERSION = 1;

    /// @notice Emitted on every successful publish (initial or update).
    /// @param who         The address that published; same as msg.sender.
    /// @param mlkemPubkey The 1184-byte ML-KEM-768 public key blob.
    /// @param version     Schema version; bumped if we ever encode differently.
    event KeyPublished(address indexed who, bytes mlkemPubkey, uint64 version);

    mapping(address => bytes) private _mlkemPubkey;

    /// @notice Publish (or overwrite) msg.sender's ML-KEM-768 public key.
    /// @param  mlkemPubkey Must be exactly 1184 bytes.
    function publish(bytes calldata mlkemPubkey) external {
        require(
            mlkemPubkey.length == ML_KEM_768_PUBKEY_LENGTH,
            "KeyRegistry: wrong pubkey length"
        );
        _mlkemPubkey[msg.sender] = mlkemPubkey;
        emit KeyPublished(msg.sender, mlkemPubkey, KEY_VERSION);
    }

    /// @notice Return the ML-KEM public key registered for `who`, or empty
    ///         bytes if none has been published.
    function get(address who) external view returns (bytes memory) {
        return _mlkemPubkey[who];
    }
}
