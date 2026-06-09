// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

import {Test} from "forge-std/Test.sol";
import {AgentDirectory} from "../src/AgentDirectory.sol";

contract AgentDirectoryTest is Test {
    AgentDirectory private dir;

    address private alice = makeAddr("alice");
    address private bob = makeAddr("bob");
    address private carol = makeAddr("carol");

    event HandleClaimed(string indexed handleHash, address indexed owner, string handle);
    event HandleTransferred(
        string indexed handleHash,
        address indexed from,
        address indexed to,
        string handle
    );

    function setUp() public {
        dir = new AgentDirectory();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Happy path
    // ─────────────────────────────────────────────────────────────────────

    function test_claim_sets_both_mappings() public {
        vm.prank(alice);
        dir.claim("alice-ai");

        assertEq(dir.handleToAddress("alice-ai"), alice);
        assertEq(dir.addressToHandle(alice), "alice-ai");
    }

    function test_claim_emits_event_with_handle_and_hash() public {
        // The indexed string topic is keccak256(handle); the data field is
        // the raw string. We assert exact bytes by including the data check.
        vm.expectEmit(true, true, false, true);
        emit HandleClaimed("alice-ai", alice, "alice-ai");

        vm.prank(alice);
        dir.claim("alice-ai");
    }

    function test_claim_accepts_min_length_handle() public {
        vm.prank(alice);
        dir.claim("abc"); // 3 chars
        assertEq(dir.handleToAddress("abc"), alice);
    }

    function test_claim_accepts_max_length_handle() public {
        // 32 chars exactly.
        string memory h = "abcdefghijklmnopqrstuvwxyz0123ab";
        assertEq(bytes(h).length, 32);

        vm.prank(alice);
        dir.claim(h);
        assertEq(dir.handleToAddress(h), alice);
    }

    function test_claim_accepts_internal_hyphens_and_digits() public {
        vm.prank(alice);
        dir.claim("gpt5-instance-42");
        assertEq(dir.handleToAddress("gpt5-instance-42"), alice);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Uniqueness invariants
    // ─────────────────────────────────────────────────────────────────────

    function test_claim_reverts_when_address_already_has_handle() public {
        vm.prank(alice);
        dir.claim("alice-ai");

        vm.expectRevert(bytes("AgentDirectory: address already has a handle"));
        vm.prank(alice);
        dir.claim("alice-2");
    }

    function test_claim_reverts_when_handle_already_taken() public {
        vm.prank(alice);
        dir.claim("the-handle");

        vm.expectRevert(bytes("AgentDirectory: handle taken"));
        vm.prank(bob);
        dir.claim("the-handle");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Length validation
    // ─────────────────────────────────────────────────────────────────────

    function test_claim_reverts_when_too_short() public {
        vm.expectRevert(bytes("AgentDirectory: handle length must be 3..32"));
        vm.prank(alice);
        dir.claim("ab"); // 2 chars
    }

    function test_claim_reverts_when_too_long() public {
        // 33 chars.
        string memory h = "abcdefghijklmnopqrstuvwxyz0123abc";
        assertEq(bytes(h).length, 33);

        vm.expectRevert(bytes("AgentDirectory: handle length must be 3..32"));
        vm.prank(alice);
        dir.claim(h);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Character-set validation
    // ─────────────────────────────────────────────────────────────────────

    function test_claim_reverts_on_uppercase() public {
        vm.expectRevert(bytes("AgentDirectory: handle must match [a-z0-9-]"));
        vm.prank(alice);
        dir.claim("Alice");
    }

    function test_claim_reverts_on_dot() public {
        vm.expectRevert(bytes("AgentDirectory: handle must match [a-z0-9-]"));
        vm.prank(alice);
        dir.claim("alice.ai");
    }

    function test_claim_reverts_on_space() public {
        vm.expectRevert(bytes("AgentDirectory: handle must match [a-z0-9-]"));
        vm.prank(alice);
        dir.claim("alice ai");
    }

    function test_claim_reverts_on_underscore() public {
        vm.expectRevert(bytes("AgentDirectory: handle must match [a-z0-9-]"));
        vm.prank(alice);
        dir.claim("alice_ai");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Leading / trailing hyphen
    // ─────────────────────────────────────────────────────────────────────

    function test_claim_reverts_on_leading_hyphen() public {
        vm.expectRevert(bytes("AgentDirectory: handle cannot start or end with -"));
        vm.prank(alice);
        dir.claim("-alice");
    }

    function test_claim_reverts_on_trailing_hyphen() public {
        vm.expectRevert(bytes("AgentDirectory: handle cannot start or end with -"));
        vm.prank(alice);
        dir.claim("alice-");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Read paths
    // ─────────────────────────────────────────────────────────────────────

    function test_read_unknown_handle_returns_zero_address() public view {
        assertEq(dir.handleToAddress("nobody"), address(0));
    }

    function test_read_unknown_address_returns_empty_string() public view {
        assertEq(dir.addressToHandle(alice), "");
    }

    // ─────────────────────────────────────────────────────────────────────
    // transfer (v2)
    // ─────────────────────────────────────────────────────────────────────

    function test_transfer_moves_handle_and_frees_old_owner() public {
        vm.prank(alice);
        dir.claim("alice-ai");

        vm.prank(alice);
        dir.transfer("alice-ai", bob);

        assertEq(dir.handleToAddress("alice-ai"), bob);
        assertEq(dir.addressToHandle(bob), "alice-ai");
        assertEq(dir.addressToHandle(alice), "", "old owner must be freed");
    }

    function test_transfer_emits_event() public {
        vm.prank(alice);
        dir.claim("alice-ai");

        vm.expectEmit(true, true, true, true);
        emit HandleTransferred("alice-ai", alice, bob, "alice-ai");

        vm.prank(alice);
        dir.transfer("alice-ai", bob);
    }

    function test_old_owner_can_claim_again_after_transfer() public {
        vm.prank(alice);
        dir.claim("alice-ai");
        vm.prank(alice);
        dir.transfer("alice-ai", bob);

        vm.prank(alice);
        dir.claim("alice-v2");
        assertEq(dir.handleToAddress("alice-v2"), alice);
    }

    function test_new_owner_can_transfer_onward() public {
        vm.prank(alice);
        dir.claim("alice-ai");
        vm.prank(alice);
        dir.transfer("alice-ai", bob);

        vm.prank(bob);
        dir.transfer("alice-ai", carol);
        assertEq(dir.handleToAddress("alice-ai"), carol);
        assertEq(dir.addressToHandle(carol), "alice-ai");
    }

    function test_transfer_reverts_when_caller_not_owner() public {
        vm.prank(alice);
        dir.claim("alice-ai");

        vm.expectRevert(bytes("AgentDirectory: not handle owner"));
        vm.prank(bob);
        dir.transfer("alice-ai", carol);
    }

    function test_transfer_reverts_on_unclaimed_handle() public {
        vm.expectRevert(bytes("AgentDirectory: not handle owner"));
        vm.prank(alice);
        dir.transfer("nobody", bob);
    }

    function test_transfer_reverts_on_zero_new_owner() public {
        vm.prank(alice);
        dir.claim("alice-ai");

        vm.expectRevert(bytes("AgentDirectory: zero new owner"));
        vm.prank(alice);
        dir.transfer("alice-ai", address(0));
    }

    function test_transfer_reverts_on_self_transfer() public {
        vm.prank(alice);
        dir.claim("alice-ai");

        vm.expectRevert(bytes("AgentDirectory: transfer to self"));
        vm.prank(alice);
        dir.transfer("alice-ai", alice);
    }

    function test_transfer_reverts_when_new_owner_has_handle() public {
        vm.prank(alice);
        dir.claim("alice-ai");
        vm.prank(bob);
        dir.claim("bob-ai");

        vm.expectRevert(bytes("AgentDirectory: new owner already has a handle"));
        vm.prank(alice);
        dir.transfer("alice-ai", bob);
    }
}
