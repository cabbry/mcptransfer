// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

import {Test, Vm} from "forge-std/Test.sol";
import {FileRegistry} from "../src/FileRegistry.sol";

contract FileRegistryTest is Test {
    FileRegistry private registry;

    address private alice = makeAddr("alice");
    address private bob = makeAddr("bob");
    address private carol = makeAddr("carol");

    // Mirrored to allow vm.expectEmit comparisons.
    event FileSent(
        address indexed from,
        address indexed to,
        string cid,
        bytes32 contentHash,
        uint64 timestamp
    );

    function setUp() public {
        registry = new FileRegistry();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Happy path
    // ─────────────────────────────────────────────────────────────────────

    function test_send_emits_event_with_correct_fields() public {
        string memory cid = "bafyabc123";
        bytes32 hash_ = keccak256("canonical manifest bytes");

        vm.warp(1_700_000_000);

        vm.expectEmit(true, true, false, true);
        emit FileSent(alice, bob, cid, hash_, 1_700_000_000);

        vm.prank(alice);
        registry.send(bob, cid, hash_);
    }

    function test_anyone_can_send() public {
        vm.prank(alice);
        registry.send(bob, "cid-a", keccak256("a"));

        vm.prank(carol);
        registry.send(bob, "cid-b", keccak256("b"));
        // No reverts -> anyone can send to anyone.
    }

    // ─────────────────────────────────────────────────────────────────────
    // Input validation
    // ─────────────────────────────────────────────────────────────────────

    function test_send_reverts_on_zero_recipient() public {
        vm.expectRevert(bytes("FileRegistry: zero recipient"));
        vm.prank(alice);
        registry.send(address(0), "cid", keccak256("x"));
    }

    function test_send_reverts_on_empty_cid() public {
        vm.expectRevert(bytes("FileRegistry: empty cid"));
        vm.prank(alice);
        registry.send(bob, "", keccak256("x"));
    }

    function test_send_reverts_on_zero_content_hash() public {
        vm.expectRevert(bytes("FileRegistry: zero content hash"));
        vm.prank(alice);
        registry.send(bob, "cid", bytes32(0));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Indexed filtering — the recipient should be able to scan only their inbox
    // ─────────────────────────────────────────────────────────────────────

    function test_event_indexed_filtering_by_recipient() public {
        vm.recordLogs();

        vm.prank(alice);
        registry.send(bob, "to-bob-1", keccak256("1"));
        vm.prank(alice);
        registry.send(carol, "to-carol", keccak256("c"));
        vm.prank(alice);
        registry.send(bob, "to-bob-2", keccak256("2"));

        Vm.Log[] memory logs = vm.getRecordedLogs();
        assertEq(logs.length, 3, "expected 3 emitted FileSent events");

        // topic0 = event signature, topic1 = indexed from, topic2 = indexed to.
        bytes32 bobTopic = bytes32(uint256(uint160(bob)));
        uint256 bobCount;
        for (uint256 i; i < logs.length; ++i) {
            if (logs[i].topics[2] == bobTopic) {
                ++bobCount;
            }
        }
        assertEq(bobCount, 2, "expected 2 events addressed to bob");
    }
}
