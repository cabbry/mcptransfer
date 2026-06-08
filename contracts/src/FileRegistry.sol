// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

/// @title  FileRegistry
/// @notice Immutable event-only registry: every file transfer announces the
///         pinned-manifest CID and its keccak-256 content hash on chain so
///         recipients can index their inbox without scanning IPFS.
/// @dev    No storage, no admin role, no upgradeability, no fee. The contract
///         is an on-chain bulletin board addressable by indexed recipient.
contract FileRegistry {
    /// @notice Emitted by `send`: one event = one file transfer announcement.
    /// @param from        msg.sender — the sender's Ethereum address.
    /// @param to          The recipient's Ethereum address.
    /// @param cid         IPFS CID of the SignedManifest blob.
    /// @param contentHash keccak256 of the canonical manifest bytes (binds the
    ///                    event to a specific manifest snapshot).
    /// @param timestamp   block.timestamp at emission, in seconds.
    event FileSent(
        address indexed from,
        address indexed to,
        string cid,
        bytes32 contentHash,
        uint64 timestamp
    );

    /// @notice Announce a file transfer to `to`. msg.sender is the announced
    ///         sender — there is no way to spoof another address.
    /// @param to          Recipient address. Cannot be the zero address.
    /// @param cid         IPFS CID of the signed manifest. Cannot be empty.
    /// @param contentHash keccak256 of the canonical manifest bytes. Cannot be zero.
    function send(address to, string calldata cid, bytes32 contentHash) external {
        require(to != address(0), "FileRegistry: zero recipient");
        require(bytes(cid).length > 0, "FileRegistry: empty cid");
        require(contentHash != bytes32(0), "FileRegistry: zero content hash");
        emit FileSent(msg.sender, to, cid, contentHash, uint64(block.timestamp));
    }
}
