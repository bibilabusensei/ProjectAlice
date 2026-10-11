"""Apply an explicit, hash-locked material-only plan to a new GLB/VRM copy.

The script never guesses from names, never edits pixels/UVs/geometry, never
overwrites either file, and preserves every non-JSON chunk byte for byte.
Private repair plans and generated models must remain outside source control.
"""
import argparse
import copy
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct

JSON_CHUNK = 0x4E4F534A
MAX_JSON_BYTES = 64 * 1024 * 1024
COPY_BLOCK_BYTES = 1024 * 1024


def hash_stream(stream, start=0):
    stream.seek(start)
    digest = hashlib.sha256()
    for data in iter(lambda: stream.read(COPY_BLOCK_BYTES), b""):
        digest.update(data)
    return digest.hexdigest()


def read_document(stream):
    stream.seek(0)
    header = stream.read(20)
    if len(header) != 20:
        raise ValueError("Truncated GLB header.")
    magic, version, total, json_length, json_type = struct.unpack("<5I", header)
    if magic != 0x46546C67 or version != 2 or json_type != JSON_CHUNK:
        raise ValueError("A GLB 2 / VRM file with a first JSON chunk is required.")
    actual_size = os.fstat(stream.fileno()).st_size
    if total != actual_size or json_length == 0 or json_length > MAX_JSON_BYTES or json_length % 4:
        raise ValueError("Invalid GLB file length or JSON chunk length.")
    if 20 + json_length > total:
        raise ValueError("JSON chunk exceeds the GLB boundary.")
    raw = stream.read(json_length)
    document = json.loads(raw.decode("utf-8"))
    if not isinstance(document, dict):
        raise ValueError("The glTF document must be an object.")
    tail_start = stream.tell()
    offset = tail_start
    while offset < total:
        stream.seek(offset)
        chunk_header = stream.read(8)
        if len(chunk_header) != 8:
            raise ValueError("Truncated non-JSON chunk header.")
        length, kind = struct.unpack("<2I", chunk_header)
        if kind == JSON_CHUNK or length % 4 or offset + 8 + length > total:
            raise ValueError("Invalid additional GLB chunk.")
        offset += 8 + length
    if offset != total:
        raise ValueError("GLB chunks do not cover the declared file length.")
    return document, tail_start, total


def apply_plan(document, plan):
    if not isinstance(plan, dict) or plan.get("schemaVersion") != 1:
        raise ValueError("Repair plan schemaVersion must be 1.")
    if set(plan) - {"schemaVersion", "expectedSourceSHA256", "materialChanges", "description"}:
        raise ValueError("Unknown repair-plan fields.")
    expected_hash = plan.get("expectedSourceSHA256", "")
    if not isinstance(expected_hash, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", expected_hash):
        raise ValueError("A complete expectedSourceSHA256 is required.")
    changes = plan.get("materialChanges")
    materials = document.get("materials")
    textures = document.get("textures", [])
    if not isinstance(changes, list) or not changes or not isinstance(materials, list):
        raise ValueError("An explicit, non-empty materialChanges array is required.")
    result = copy.deepcopy(document)
    changed_indices = set()
    allowed = {"index", "expectedName", "baseColorTexture", "alphaMode", "alphaCutoff", "unlit"}
    for change in changes:
        if not isinstance(change, dict) or set(change) - allowed:
            raise ValueError("Unknown material-change fields; geometry edits are not allowed.")
        index = change.get("index")
        if type(index) is not int or index < 0 or index >= len(materials) or index in changed_indices:
            raise ValueError("Invalid or duplicated material index.")
        name = change.get("expectedName")
        if not isinstance(name, str) or materials[index].get("name") != name:
            raise ValueError(f"Material {index} does not match expectedName.")
        if not set(change) - {"index", "expectedName"}:
            raise ValueError("Each material change must specify an actual repair.")
        material = result["materials"][index]
        if "baseColorTexture" in change:
            texture = change["baseColorTexture"]
            if type(texture) is not int or texture < 0 or texture >= len(textures):
                raise ValueError("Replacement texture must refer to an existing texture.")
            pbr = material.setdefault("pbrMetallicRoughness", {})
            pbr.setdefault("baseColorTexture", {})["index"] = texture
        if "alphaMode" in change:
            mode = change["alphaMode"]
            if mode not in ("OPAQUE", "MASK", "BLEND"):
                raise ValueError("Invalid glTF alphaMode.")
            material["alphaMode"] = mode
        if "alphaCutoff" in change:
            cutoff = change["alphaCutoff"]
            if type(cutoff) not in (int, float) or not math.isfinite(cutoff) or not 0 <= cutoff <= 1:
                raise ValueError("alphaCutoff must be finite and between 0 and 1.")
            material["alphaCutoff"] = cutoff
        if "unlit" in change:
            if change["unlit"] is not True:
                raise ValueError("unlit must be true when explicitly added.")
            material.setdefault("extensions", {})["KHR_materials_unlit"] = {}
            used = result.setdefault("extensionsUsed", [])
            if "KHR_materials_unlit" not in used:
                used.append("KHR_materials_unlit")
        changed_indices.add(index)
    return result, sorted(changed_indices)


def repair(input_path, output_path, plan):
    source = Path(input_path).resolve(strict=True)
    output = Path(output_path).resolve()
    if os.path.normcase(str(source)) == os.path.normcase(str(output)):
        raise ValueError("Output must be a separate copy, never the original model.")
    if output.exists():
        raise FileExistsError("Output already exists; refusing to overwrite it.")
    with source.open("rb") as stream:
        document, tail_start, total = read_document(stream)
        patched, changed_indices = apply_plan(document, plan)
        source_hash = hash_stream(stream)
        if source_hash.casefold() != plan["expectedSourceSHA256"].casefold():
            raise ValueError("Source SHA-256 does not match the reviewed repair plan.")
        tail_hash = hash_stream(stream, tail_start)
        encoded = json.dumps(patched, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
        encoded += b" " * (-len(encoded) % 4)
        output_size = 20 + len(encoded) + total - tail_start
        if len(encoded) > MAX_JSON_BYTES or output_size > 0xFFFFFFFF:
            raise ValueError("Patched document exceeds GLB limits.")
        output.parent.mkdir(parents=True, exist_ok=True)
        created = False
        try:
            with output.open("xb") as destination:
                created = True
                destination.write(struct.pack("<5I", 0x46546C67, 2, output_size, len(encoded), JSON_CHUNK))
                destination.write(encoded)
                stream.seek(tail_start)
                for data in iter(lambda: stream.read(COPY_BLOCK_BYTES), b""):
                    destination.write(data)
            if hash_stream(stream) != source_hash:
                raise ValueError("Source changed during repair; rejecting the generated copy.")
            with output.open("rb") as verification:
                _, output_tail, _ = read_document(verification)
                if hash_stream(verification, output_tail) != tail_hash:
                    raise ValueError("Non-JSON data changed; rejecting the generated copy.")
                output_hash = hash_stream(verification)
        except Exception:
            if created:
                output.unlink(missing_ok=True)
            raise
    return {"input": str(source), "output": str(output), "sourceSHA256": source_hash,
        "outputSHA256": output_hash, "nonJSONChunksSHA256": tail_hash,
        "nonJSONChunksUnchanged": True, "materialIndices": changed_indices,
        "originalUnchanged": True, "sizeBytes": output_size}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, help="Original local GLB or VRM (read only).")
    parser.add_argument("--output", required=True, help="New local output copy; must not exist.")
    parser.add_argument("--plan", required=True, help="Explicit JSON repair plan, preferably in private-models/.")
    args = parser.parse_args()
    with Path(args.plan).open("r", encoding="utf-8-sig") as plan_file:
        plan = json.load(plan_file)
    print(json.dumps(repair(args.input, args.output, plan), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
