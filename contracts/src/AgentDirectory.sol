// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

/// @title  AgentDirectory
/// @notice First-come-first-served `handle ↔ address` registry. Handles look
///         like `alice-ai`, `gpt5-instance-42` — lowercase alphanumeric with
///         optional internal hyphens. Resolves both ways so a CLI can show
///         `0x… (alice-ai)` and a sender can address by handle.
/// @dev    Permanent, non-transferable in v1: once claimed, the handle is
///         bound to the address forever, and the address can never claim a
///         second handle. No admin, no fee, no upgradeability.
contract AgentDirectory {
    /// @notice Emitted when an address successfully claims a handle.
    /// @param handleHash The keccak256 hash of the handle string — indexed so
    ///                   off-chain consumers can filter by handle without
    ///                   storing the string itself in a log topic.
    /// @param owner      The claiming address (msg.sender).
    /// @param handle     The original handle string (non-indexed, readable).
    event HandleClaimed(string indexed handleHash, address indexed owner, string handle);

    /// @notice handle → owner address. Zero address means unclaimed.
    mapping(string => address) public handleToAddress;

    /// @notice owner address → handle. Empty string means no handle.
    mapping(address => string) public addressToHandle;

    /// @notice Minimum handle length (inclusive).
    uint256 public constant MIN_HANDLE_LENGTH = 3;
    /// @notice Maximum handle length (inclusive).
    uint256 public constant MAX_HANDLE_LENGTH = 32;

    /// @notice Claim `handle` for msg.sender. The handle must:
    ///         - be 3..32 bytes long,
    ///         - contain only lowercase ASCII letters, digits, and hyphens,
    ///         - not start or end with a hyphen.
    /// @dev    msg.sender must not already own a handle, and the handle must
    ///         not already be taken.
    function claim(string calldata handle) external {
        require(
            bytes(addressToHandle[msg.sender]).length == 0,
            "AgentDirectory: address already has a handle"
        );
        require(
            handleToAddress[handle] == address(0),
            "AgentDirectory: handle taken"
        );
        _validateHandle(handle);

        handleToAddress[handle] = msg.sender;
        addressToHandle[msg.sender] = handle;

        emit HandleClaimed(handle, msg.sender, handle);
    }

    /// @dev Reverts if `h` does not satisfy [a-z0-9-]{3,32} with no leading
    ///      or trailing hyphen.
    function _validateHandle(string calldata h) private pure {
        bytes calldata b = bytes(h);
        require(
            b.length >= MIN_HANDLE_LENGTH && b.length <= MAX_HANDLE_LENGTH,
            "AgentDirectory: handle length must be 3..32"
        );
        require(
            b[0] != 0x2D && b[b.length - 1] != 0x2D,
            "AgentDirectory: handle cannot start or end with -"
        );
        for (uint256 i; i < b.length; ++i) {
            bytes1 c = b[i];
            bool ok = (c >= 0x30 && c <= 0x39)     // 0-9
                   || (c >= 0x61 && c <= 0x7A)     // a-z
                   || c == 0x2D;                    // -
            require(ok, "AgentDirectory: handle must match [a-z0-9-]");
        }
    }
}
