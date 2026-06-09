// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

import {Test} from "forge-std/Test.sol";
import {Blocklist} from "../src/Blocklist.sol";

contract BlocklistTest is Test {
    Blocklist private list;

    address private alice = makeAddr("alice");
    address private bob = makeAddr("bob");
    address private carol = makeAddr("carol");

    event BlockSet(address indexed recipient, address indexed sender, bool blocked);

    function setUp() public {
        list = new Blocklist();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Happy path
    // ─────────────────────────────────────────────────────────────────────

    function test_default_state_is_unblocked() public view {
        assertFalse(list.isBlocked(alice, bob));
    }

    function test_block_then_unblock_round_trips() public {
        vm.prank(bob);
        list.setBlocked(alice, true);
        assertTrue(list.isBlocked(bob, alice));

        vm.prank(bob);
        list.setBlocked(alice, false);
        assertFalse(list.isBlocked(bob, alice));
    }

    function test_set_emits_event() public {
        vm.expectEmit(true, true, false, true);
        emit BlockSet(bob, alice, true);

        vm.prank(bob);
        list.setBlocked(alice, true);
    }

    function test_set_is_idempotent() public {
        vm.prank(bob);
        list.setBlocked(alice, true);
        vm.prank(bob);
        list.setBlocked(alice, true); // same state again succeeds
        assertTrue(list.isBlocked(bob, alice));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Isolation: each recipient edits only its own list
    // ─────────────────────────────────────────────────────────────────────

    function test_block_only_affects_callers_own_list() public {
        vm.prank(bob);
        list.setBlocked(alice, true);

        assertTrue(list.isBlocked(bob, alice));
        assertFalse(list.isBlocked(carol, alice), "carol's list must be untouched");
        assertFalse(list.isBlocked(alice, bob), "direction matters");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Validation
    // ─────────────────────────────────────────────────────────────────────

    function test_set_reverts_on_zero_sender() public {
        vm.expectRevert(bytes("Blocklist: zero sender"));
        vm.prank(bob);
        list.setBlocked(address(0), true);
    }

    function test_set_reverts_on_self_block() public {
        vm.expectRevert(bytes("Blocklist: cannot block self"));
        vm.prank(bob);
        list.setBlocked(bob, true);
    }
}
