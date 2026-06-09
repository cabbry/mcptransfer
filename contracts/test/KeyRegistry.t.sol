// SPDX-License-Identifier: MIT
pragma solidity ^0.8.27;

import {Test} from "forge-std/Test.sol";
import {KeyRegistry} from "../src/KeyRegistry.sol";

contract KeyRegistryTest is Test {
    KeyRegistry private registry;

    address private alice = makeAddr("alice");
    address private bob = makeAddr("bob");

    event KeysPublished(
        address indexed who,
        bytes secp256k1Pubkey,
        bytes32 mlkemHash,
        string mlkemCid,
        uint64 version
    );

    function setUp() public {
        registry = new KeyRegistry();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Happy path
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_then_get_round_trips_entry() public {
        bytes memory ecPk = _fakeSecp256k1Pubkey(0xAA);
        bytes32 mlHash = keccak256(_fakeMlKemPubkey(0xBB));
        string memory cid = "bafybeigdyrztexamplecidforthekeyblob";

        vm.prank(alice);
        registry.publish(ecPk, mlHash, cid);

        bytes memory storedEc = registry.getSecp256k1(alice);
        (bytes32 storedHash, string memory storedCid) = registry.getMlKem(alice);
        assertEq(storedEc.length, registry.SECP256K1_COMPRESSED_LENGTH());
        assertEq(keccak256(storedEc), keccak256(ecPk), "stored secp256k1 must match input");
        assertEq(storedHash, mlHash, "stored mlkem hash must match input");
        assertEq(storedCid, cid, "stored mlkem cid must match input");
    }

    function test_commitment_matches_offchain_key() public {
        // The hash a reader recomputes over the fetched key bytes must equal
        // the stored commitment — this is the verification clients perform.
        bytes memory mlkemKey = _fakeMlKemPubkey(0xCD);

        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0x11), keccak256(mlkemKey), "cid-1");

        (bytes32 storedHash,) = registry.getMlKem(alice);
        assertEq(storedHash, keccak256(mlkemKey));
        assertTrue(storedHash != keccak256(_fakeMlKemPubkey(0xCE)), "tampered key must not match");
    }

    function test_publish_emits_event_with_all_fields() public {
        bytes memory ecPk = _fakeSecp256k1Pubkey(0x11);
        bytes32 mlHash = keccak256(_fakeMlKemPubkey(0xCD));

        vm.expectEmit(true, false, false, true);
        emit KeysPublished(alice, ecPk, mlHash, "cid-1", 2);

        vm.prank(alice);
        registry.publish(ecPk, mlHash, "cid-1");
    }

    function test_publish_overwrites_previous_entry_for_same_address() public {
        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0x11), keccak256("k1"), "cid-1");
        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0x22), keccak256("k2"), "cid-2");

        assertEq(keccak256(registry.getSecp256k1(alice)), keccak256(_fakeSecp256k1Pubkey(0x22)));
        (bytes32 storedHash, string memory storedCid) = registry.getMlKem(alice);
        assertEq(storedHash, keccak256("k2"));
        assertEq(storedCid, "cid-2");
    }

    // ─────────────────────────────────────────────────────────────────────
    // msg.sender binding
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_only_updates_msg_sender_entry() public {
        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0xA1), keccak256("k"), "cid");

        assertEq(registry.getSecp256k1(bob).length, 0, "bob secp256k1 must stay empty");
        (bytes32 bobHash, string memory bobCid) = registry.getMlKem(bob);
        assertEq(bobHash, bytes32(0), "bob mlkem hash must stay zero");
        assertEq(bytes(bobCid).length, 0, "bob mlkem cid must stay empty");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Read paths
    // ─────────────────────────────────────────────────────────────────────

    function test_get_returns_empty_for_unknown_address() public view {
        assertEq(registry.getSecp256k1(alice).length, 0);
        (bytes32 h, string memory c) = registry.getMlKem(alice);
        assertEq(h, bytes32(0));
        assertEq(bytes(c).length, 0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Validation — secp256k1
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_reverts_on_short_secp256k1() public {
        vm.expectRevert(bytes("KeyRegistry: wrong secp256k1 length"));
        vm.prank(alice);
        registry.publish(new bytes(32), keccak256("k"), "cid");
    }

    function test_publish_reverts_on_long_secp256k1() public {
        vm.expectRevert(bytes("KeyRegistry: wrong secp256k1 length"));
        vm.prank(alice);
        registry.publish(new bytes(65), keccak256("k"), "cid");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Validation — mlkem commitment
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_reverts_on_zero_mlkem_hash() public {
        vm.expectRevert(bytes("KeyRegistry: zero mlkem hash"));
        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0x11), bytes32(0), "cid");
    }

    function test_publish_reverts_on_empty_cid() public {
        vm.expectRevert(bytes("KeyRegistry: empty mlkem cid"));
        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0x11), keccak256("k"), "");
    }

    function test_publish_reverts_on_oversize_cid() public {
        bytes memory big = new bytes(129);
        for (uint256 i; i < big.length; ++i) big[i] = "a";

        vm.expectRevert(bytes("KeyRegistry: cid too long"));
        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0x11), keccak256("k"), string(big));
    }

    function test_publish_accepts_max_length_cid() public {
        bytes memory max = new bytes(128);
        for (uint256 i; i < max.length; ++i) max[i] = "a";

        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0x11), keccak256("k"), string(max));

        (, string memory storedCid) = registry.getMlKem(alice);
        assertEq(bytes(storedCid).length, 128);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    function _fakeSecp256k1Pubkey(uint8 seed) private pure returns (bytes memory) {
        bytes memory pk = new bytes(33);
        pk[0] = 0x02; // compressed prefix
        for (uint256 i = 1; i < pk.length; ++i) {
            pk[i] = bytes1(uint8(uint256(keccak256(abi.encode(seed, i)))));
        }
        return pk;
    }

    function _fakeMlKemPubkey(uint8 seed) private pure returns (bytes memory) {
        bytes memory pk = new bytes(1184);
        for (uint256 i; i < pk.length; ++i) {
            pk[i] = bytes1(uint8(uint256(keccak256(abi.encode(seed, i)))));
        }
        return pk;
    }
}
