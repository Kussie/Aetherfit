#!/usr/bin/env python3
"""Regenerate Aetherfit/Resources/composite_tags.json.

Builds the leaf-tag -> "category/type" map (e.g. "micro bikini" -> "swimsuit/bikini") from
Danbooru's active tag-implication graph, intersected with the WD-v3 tagger vocabulary.

Run:  python tools/generate_composite_tags.py            (fetches live, caches the dump)
      python tools/generate_composite_tags.py --refresh  (ignore the cache)

Tuning knobs are the four sets below: ROOTS, SYNTHETIC, COLORS, NOISE.
"""
import json, time, urllib.request, csv, io, collections, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "Aetherfit", "Resources", "composite_tags.json")
CACHE = os.path.join(HERE, "danbooru_cache.json")
UA = {"User-Agent": "Aetherfit-composite-gen/1.0"}
VOCAB_URL = "https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/resolve/main/selected_tags.csv"

# Top-level categories that anchor a composite. Intermediate types (bikini, kimono, bra...) must NOT
# be here, or their variants group under them instead of collapsing to the category.
ROOTS = {
    "swimsuit","dress","skirt","shirt","shorts","pants","jacket","coat","sweater",
    "thighhighs","pantyhose","socks","kneehighs","gloves","hat","shoes","boots","leotard",
    "bodysuit","japanese_clothes","apron","cape","scarf","necktie",
    "underwear","lingerie","robe","vest","hoodie","cardigan","overalls","jumpsuit","romper",
    "school_uniform","military_uniform","kilt","loincloth","poncho","cloak","tabard","suit",
}

# Danbooru models some tags as top-level siblings rather than children, so the graph never links
# them to a category. Add synthetic child -> parent edges to regroup them (e.g. heels under shoes).
SYNTHETIC = {
    "high_heels": "shoes",
    "strappy_heels": "shoes",
    "platform_heels": "shoes",
    "sandals": "shoes",
}

# Colour adjectives stripped from a type so "black_dress" yields nothing but "china_dress" survives.
COLORS = {
    "black","blue","brown","green","grey","gray","orange","pink","purple","red","white",
    "yellow","aqua","gold","silver","multicolored","two-toned","rainbow","tan","beige",
    "light_blue","dark_blue","dark_green","light_brown","light_green",
}

# State / pose / partial tags that imply a garment but aren't a subtype of it.
NOISE = re.compile(
    r"(unworn_|adjusting_|torn_|taut_|impossible_|hand_under_|_under_|_under$|_lift$|_pull$|"
    r"_only$|_removed$|_aside$|untied_|_in_mouth$|dressing|undressing|clothes_removed|"
    r"_around_|holding_|naked_|nearly_naked|_handjob|_over_|panties_over|_tug$|_grab$|"
    r"presenting|_slip$|no_)"
)


def fetch(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=60) as r:
        return r.read()


def load_data(refresh):
    if os.path.exists(CACHE) and not refresh:
        blob = json.load(open(CACHE))
        return set(blob["vocab"]), blob["imp"]

    print("fetching tagger vocab...", file=sys.stderr)
    vocab = {r["name"] for r in csv.DictReader(io.StringIO(fetch(VOCAB_URL).decode())) if r["category"] == "0"}

    print("fetching Danbooru implications...", file=sys.stderr)
    imp, last_id = collections.defaultdict(list), None
    while True:
        url = "https://danbooru.donmai.us/tag_implications.json?search%5Bstatus%5D=active&limit=1000"
        if last_id is not None:
            url += f"&page=b{last_id}"
        data = json.loads(fetch(url))
        if not data:
            break
        for d in data:
            imp[d["antecedent_name"]].append(d["consequent_name"])
            last_id = d["id"] if last_id is None else min(last_id, d["id"])
        if len(data) < 1000:
            break
        time.sleep(0.3)
    json.dump({"vocab": sorted(vocab), "imp": imp}, open(CACHE, "w"))
    return vocab, dict(imp)


def strip_color(tag):
    parts = tag.split("_")
    while len(parts) > 1 and parts[0] in COLORS:
        parts = parts[1:]
    return "_".join(parts)


def find_path(leaf, imp):
    # Shortest chain leaf..root that ends in a ROOT.
    best, stack, seen = None, [(leaf, [leaf])], set()
    while stack:
        cur, path = stack.pop()
        if len(path) > 8 or cur in seen:
            continue
        seen.add(cur)
        if cur in ROOTS and len(path) > 1:
            if best is None or len(path) < len(best):
                best = path
            continue
        for p in imp.get(cur, []):
            stack.append((p, path + [p]))
    return best


def main():
    vocab, imp = load_data("--refresh" in sys.argv)
    for child, parent in SYNTHETIC.items():
        imp.setdefault(child, [])
        if parent not in imp[child]:
            imp[child].insert(0, parent)

    mapping = {}
    for leaf in vocab:
        if leaf in ROOTS or NOISE.search(leaf):
            continue
        path = find_path(leaf, imp)
        if not path:
            continue
        root, node_below = path[-1], path[-2]
        typ = strip_color(node_below)
        if not typ or typ == root or typ in COLORS:
            continue
        mapping[leaf.replace("_", " ")] = f"{root}/{typ}".replace("_", " ")

    out = dict(sorted(mapping.items()))
    json.dump(out, open(OUT, "w"), indent=0, ensure_ascii=False)
    print(f"wrote {len(out)} leaves -> {len(set(out.values()))} composites to {os.path.normpath(OUT)}")


if __name__ == "__main__":
    main()
