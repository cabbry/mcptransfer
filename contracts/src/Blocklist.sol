// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

/// @title  Blocklist
/// @notice Per-recipient sender blocklist. Anyone can emit a `FileSent` event
///         addressed to anyone (the FileRegistry is deliberately
///         permissionless), so spam prevention happens at READ time: a
///         recipient records which senders it ignores, and inbox readers
///         filter those events out client-side.
/// @dev    Purely advisory state — nothing on-chain prevents a blocked sender
///         from emitting more events; honest clients simply do not surface
///         them. Strictly self-service (msg.sender edits only its own list),
///         no admin, no fee, no upgradeability. Blocking is reversible.
contract Blocklist {
    /// @notice Emitted whenever a recipient changes a sender's blocked state.
    /// @param recipient The list owner (msg.sender).
    /// @param sender    The sender whose state changed.
    /// @param blocked   The new state (true = blocked).
    event BlockSet(address indexed recipient, address indexed sender, bool blocked);

    /// @notice recipient => sender => blocked.
    mapping(address => mapping(address => bool)) public isBlocked;

    /// @notice Set whether `sender` is blocked for msg.sender's inbox.
    ///         Idempotent: re-setting the current state succeeds (and
    ///         re-emits the event).
    function setBlocked(address sender, bool blocked) external {
        require(sender != address(0), "Blocklist: zero sender");
        require(sender != msg.sender, "Blocklist: cannot block self");
        isBlocked[msg.sender][sender] = blocked;
        emit BlockSet(msg.sender, sender, blocked);
    }
}
