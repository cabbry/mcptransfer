// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

import {Test} from "forge-std/Test.sol";
import {AgentDirectory} from "../src/AgentDirectory.sol";

contract AgentDirectoryTest is Test {
    AgentDirectory private dir;

    address private alice = makeAddr("alice");
    address private bob = makeAddr("bob");

    event HandleClaimed(string indexed handleHash, address indexed owner, string handle);

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
}
