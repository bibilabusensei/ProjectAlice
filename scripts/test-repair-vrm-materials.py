"""Synthetic model tests: never read or include a user's private assets."""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("material_repair", Path(__file__).with_name("repair-vrm-materials.py"))
repair_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(repair_module)


def glb(document, tail=b"unchanged pixels and geometry1234"):
    encoded = json.dumps(document).encode("utf-8")
    encoded += b" " * (-len(encoded) % 4)
    tail += b"\0" * (-len(tail) % 4)
    return struct.pack("<5I", 0x46546C67, 2, 28 + len(encoded) + len(tail), len(encoded), 0x4E4F534A) + encoded + struct.pack("<2I", len(tail), 0x004E4942) + tail


class RepairTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.source = self.root / "original.vrm"
        self.output = self.root / "repaired.vrm"
        self.document = {"asset": {"version": "2.0"}, "materials": [
            {"name": "Fabric", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0, "texCoord": 0}}},
            {"name": "Eye", "extras": {"original": "kept"}}],
            "textures": [{"source": 0}, {"source": 1}],
            "meshes": [{"primitives": [{"material": 1}]}],
            "extensions": {"VRMC_vrm": {"expressions": {"custom": {"x": {"materialColorBinds": [{"material": 1}]}}}}}}
        self.original = glb(self.document)
        self.source.write_bytes(self.original)
        self.plan = {"schemaVersion": 1, "expectedSourceSHA256": hashlib.sha256(self.original).hexdigest(),
            "materialChanges": [{"index": 0, "expectedName": "Fabric", "baseColorTexture": 1},
                {"index": 1, "expectedName": "Eye", "alphaMode": "BLEND", "unlit": True}]}

    def test_material_only_copy_and_binary_identity(self):
        report = repair_module.repair(self.source, self.output, self.plan)
        with self.source.open("rb") as a, self.output.open("rb") as b:
            original, offset_a, _ = repair_module.read_document(a)
            patched, offset_b, _ = repair_module.read_document(b)
            self.assertEqual(repair_module.hash_stream(a, offset_a), repair_module.hash_stream(b, offset_b))
        self.assertEqual(self.source.read_bytes(), self.original)
        self.assertEqual(patched["meshes"], original["meshes"])
        self.assertEqual(patched["extensions"], original["extensions"])
        self.assertEqual(len(patched["materials"]), 2)
        self.assertEqual(patched["materials"][0]["pbrMetallicRoughness"]["baseColorTexture"], {"index": 1, "texCoord": 0})
        self.assertEqual(patched["materials"][1]["alphaMode"], "BLEND")
        self.assertEqual(patched["materials"][1]["extras"], {"original": "kept"})
        self.assertEqual(patched["extensionsUsed"], ["KHR_materials_unlit"])
        self.assertTrue(report["nonJSONChunksUnchanged"])

    def test_refuses_same_path(self):
        with self.assertRaises(ValueError):
            repair_module.repair(self.source, self.source, self.plan)

    def test_refuses_existing_output(self):
        self.output.write_bytes(b"keep")
        with self.assertRaises(FileExistsError):
            repair_module.repair(self.source, self.output, self.plan)
        self.assertEqual(self.output.read_bytes(), b"keep")

    def test_hash_mismatch_has_no_output(self):
        self.plan["expectedSourceSHA256"] = "0" * 64
        with self.assertRaises(ValueError):
            repair_module.repair(self.source, self.output, self.plan)
        self.assertFalse(self.output.exists())

    def test_missing_hash(self):
        del self.plan["expectedSourceSHA256"]
        with self.assertRaises(ValueError):
            repair_module.apply_plan(self.document, self.plan)

    def test_rejects_wrong_material_name(self):
        self.plan["materialChanges"][0]["expectedName"] = "Different"
        with self.assertRaises(ValueError):
            repair_module.apply_plan(self.document, self.plan)

    def test_rejects_nonexistent_texture(self):
        self.plan["materialChanges"][0]["baseColorTexture"] = 2
        with self.assertRaises(ValueError):
            repair_module.apply_plan(self.document, self.plan)

    def test_rejects_duplicate_material(self):
        self.plan["materialChanges"].append(self.plan["materialChanges"][0])
        with self.assertRaises(ValueError):
            repair_module.apply_plan(self.document, self.plan)

    def test_rejects_geometry_changes(self):
        self.plan["materialChanges"][0]["positions"] = [0, 0, 0]
        with self.assertRaises(ValueError):
            repair_module.apply_plan(self.document, self.plan)

    def test_rejects_invalid_alpha_mode(self):
        self.plan["materialChanges"][1]["alphaMode"] = "AUTO"
        with self.assertRaises(ValueError):
            repair_module.apply_plan(self.document, self.plan)

    def test_rejects_invalid_chunk_boundary(self):
        self.source.write_bytes(self.original[:-4])
        with self.source.open("rb") as stream:
            with self.assertRaises(ValueError):
                repair_module.read_document(stream)

    def test_adds_texture_to_an_untextured_material(self):
        self.plan["materialChanges"][1]["baseColorTexture"] = 1
        patched, _ = repair_module.apply_plan(self.document, self.plan)
        self.assertEqual(patched["materials"][1]["pbrMetallicRoughness"]["baseColorTexture"]["index"], 1)
        self.assertNotIn("pbrMetallicRoughness", self.document["materials"][1])


if __name__ == "__main__":
    unittest.main(verbosity=2)
