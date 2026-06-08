// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

import {Test} from "forge-std/Test.sol";
import {KeyRegistry} from "../src/KeyRegistry.sol";

contract KeyRegistryTest is Test {
    KeyRegistry private registry;

    address private alice = makeAddr("alice");
    address private bob = makeAddr("bob");

    event KeyPublished(address indexed who, bytes mlkemPubkey, uint64 version);

    function setUp() public {
        registry = new KeyRegistry();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Happy path
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_then_get_round_trips() public {
        bytes memory pk = _fakeMlKemPubkey(0xAA);

        vm.prank(alice);
        registry.publish(pk);

        bytes memory stored = registry.get(alice);
        assertEq(stored.length, registry.ML_KEM_768_PUBKEY_LENGTH());
        assertEq(keccak256(stored), keccak256(pk), "stored pubkey must match input");
    }

    function test_publish_emits_event_with_all_fields() public {
        bytes memory pk = _fakeMlKemPubkey(0xCD);

        vm.expectEmit(true, false, false, true);
        emit KeyPublished(alice, pk, 1);

        vm.prank(alice);
        registry.publish(pk);
    }

    function test_publish_overwrites_previous_key_for_same_address() public {
        bytes memory pk1 = _fakeMlKemPubkey(0x11);
        bytes memory pk2 = _fakeMlKemPubkey(0x22);

        vm.prank(alice);
        registry.publish(pk1);
        vm.prank(alice);
        registry.publish(pk2);

        assertEq(keccak256(registry.get(alice)), keccak256(pk2), "second publish must overwrite");
    }

    // ─────────────────────────────────────────────────────────────────────
    // msg.sender binding
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_only_updates_msg_sender_entry() public {
        bytes memory pkAlice = _fakeMlKemPubkey(0xA1);

        vm.prank(alice);
        registry.publish(pkAlice);

        // Bob never published — must be empty.
        assertEq(registry.get(bob).length, 0, "bob entry must stay empty");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Read paths
    // ─────────────────────────────────────────────────────────────────────

    function test_get_returns_empty_bytes_for_unknown_address() public view {
        bytes memory pk = registry.get(alice);
        assertEq(pk.length, 0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Length validation
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_reverts_on_short_pubkey() public {
        bytes memory shortPk = new bytes(1183);
        vm.expectRevert(bytes("KeyRegistry: wrong pubkey length"));
        vm.prank(alice);
        registry.publish(shortPk);
    }

    function test_publish_reverts_on_long_pubkey() public {
        bytes memory longPk = new bytes(1185);
        vm.expectRevert(bytes("KeyRegistry: wrong pubkey length"));
        vm.prank(alice);
        registry.publish(longPk);
    }

    function test_publish_reverts_on_empty_pubkey() public {
        vm.expectRevert(bytes("KeyRegistry: wrong pubkey length"));
        vm.prank(alice);
        registry.publish("");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// @dev Deterministic 1184-byte pseudo-pubkey for testing. NOT a real
    ///      ML-KEM key — the contract never inspects internal structure.
    function _fakeMlKemPubkey(uint8 seed) private pure returns (bytes memory) {
        bytes memory pk = new bytes(1184);
        for (uint256 i; i < pk.length; ++i) {
            pk[i] = bytes1(uint8(uint256(keccak256(abi.encode(seed, i)))));
        }
        return pk;
    }
}
