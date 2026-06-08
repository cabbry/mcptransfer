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
        bytes mlkemPubkey,
        uint64 version
    );

    function setUp() public {
        registry = new KeyRegistry();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Happy path
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_then_get_round_trips_both_keys() public {
        bytes memory ecPk = _fakeSecp256k1Pubkey(0xAA);
        bytes memory mlkemPk = _fakeMlKemPubkey(0xBB);

        vm.prank(alice);
        registry.publish(ecPk, mlkemPk);

        bytes memory storedEc = registry.getSecp256k1(alice);
        bytes memory storedMlkem = registry.getMlKem(alice);
        assertEq(storedEc.length, registry.SECP256K1_COMPRESSED_LENGTH());
        assertEq(storedMlkem.length, registry.ML_KEM_768_PUBKEY_LENGTH());
        assertEq(keccak256(storedEc), keccak256(ecPk), "stored secp256k1 must match input");
        assertEq(keccak256(storedMlkem), keccak256(mlkemPk), "stored mlkem must match input");
    }

    function test_publish_emits_event_with_all_fields() public {
        bytes memory ecPk = _fakeSecp256k1Pubkey(0x11);
        bytes memory mlkemPk = _fakeMlKemPubkey(0xCD);

        vm.expectEmit(true, false, false, true);
        emit KeysPublished(alice, ecPk, mlkemPk, 1);

        vm.prank(alice);
        registry.publish(ecPk, mlkemPk);
    }

    function test_publish_overwrites_previous_keys_for_same_address() public {
        bytes memory ec1 = _fakeSecp256k1Pubkey(0x11);
        bytes memory ml1 = _fakeMlKemPubkey(0x11);
        bytes memory ec2 = _fakeSecp256k1Pubkey(0x22);
        bytes memory ml2 = _fakeMlKemPubkey(0x22);

        vm.prank(alice);
        registry.publish(ec1, ml1);
        vm.prank(alice);
        registry.publish(ec2, ml2);

        assertEq(keccak256(registry.getSecp256k1(alice)), keccak256(ec2));
        assertEq(keccak256(registry.getMlKem(alice)), keccak256(ml2));
    }

    // ─────────────────────────────────────────────────────────────────────
    // msg.sender binding
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_only_updates_msg_sender_entry() public {
        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0xA1), _fakeMlKemPubkey(0xA2));

        assertEq(registry.getSecp256k1(bob).length, 0, "bob secp256k1 must stay empty");
        assertEq(registry.getMlKem(bob).length, 0, "bob mlkem must stay empty");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Read paths
    // ─────────────────────────────────────────────────────────────────────

    function test_get_returns_empty_bytes_for_unknown_address() public view {
        assertEq(registry.getSecp256k1(alice).length, 0);
        assertEq(registry.getMlKem(alice).length, 0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Length validation — secp256k1
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_reverts_on_short_secp256k1() public {
        vm.expectRevert(bytes("KeyRegistry: wrong secp256k1 length"));
        vm.prank(alice);
        registry.publish(new bytes(32), _fakeMlKemPubkey(0xFF));
    }

    function test_publish_reverts_on_long_secp256k1() public {
        vm.expectRevert(bytes("KeyRegistry: wrong secp256k1 length"));
        vm.prank(alice);
        registry.publish(new bytes(65), _fakeMlKemPubkey(0xFF));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Length validation — mlkem
    // ─────────────────────────────────────────────────────────────────────

    function test_publish_reverts_on_short_mlkem() public {
        vm.expectRevert(bytes("KeyRegistry: wrong mlkem length"));
        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0x11), new bytes(1183));
    }

    function test_publish_reverts_on_long_mlkem() public {
        vm.expectRevert(bytes("KeyRegistry: wrong mlkem length"));
        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0x11), new bytes(1185));
    }

    function test_publish_reverts_on_empty_mlkem() public {
        vm.expectRevert(bytes("KeyRegistry: wrong mlkem length"));
        vm.prank(alice);
        registry.publish(_fakeSecp256k1Pubkey(0x11), "");
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
