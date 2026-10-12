#!/usr/bin/env python3
"""
Downloads Pokémon TCG card images to use as a card folder for Mosaic Generator.

The images come from the Pokémon TCG API (https://pokemontcg.io). They're © The Pokémon
Company, Nintendo, Creatures and GAME FREAK, so they're for your own use only: don't share or
publish them. That's also why this script exists instead of a ready-made card pack.

Each set goes in its own subfolder of the output folder. Choose the output folder as the card
folder in Mosaic Generator; subfolders are searched too.

Only needs Python 3.9 or newer, nothing to install.

Usage:
    python download_cards.py                      # Base Set + the 3 newest sets
    python download_cards.py --newest 5           # Base Set + the 5 newest sets
    python download_cards.py --sets base1 base2   # exactly these sets
    python download_cards.py --list-sets          # show all sets and their ids
    python download_cards.py --large              # full-size scans instead of small images
    python download_cards.py --out "D:/Cards"     # where to save (default: ./pokemon_cards)

Running it again only downloads what's missing, so it can be stopped and resumed at any time.
An API key (free at https://dev.pokemontcg.io) raises the API's rate limit, but isn't needed:
    set POKEMONTCG_API_KEY=yourkey      (Windows)
    export POKEMONTCG_API_KEY=yourkey   (macOS, Linux)
"""

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

API = "https://api.pokemontcg.io/v2"
USER_AGENT = "MosaicGenerator-card-downloader/1.0"

# If the API is down, Base Set can still be downloaded straight from the image server, by
# card number. The files are then named by number only, since the card names come from the API.
BASE_SET_ID = "base1"
BASE_SET_NAME = "Base"
BASE_SET_SIZE = 102
BASE_IMAGE_URL = "https://images.pokemontcg.io/base1/{number}{suffix}.png"

# Downloads at the same time. Kept low to be polite to a free service.
WORKERS = 4


class NotFound(Exception):
    pass


# ── Talking to the API ──────────────────────────────────────────────────────────────────────

def fetch(url, params=None, tries=4):
    """The body of a GET request, retrying on network errors, rate limits and server errors."""
    if params:
        url += "?" + urllib.parse.urlencode(params)
    headers = {"User-Agent": USER_AGENT}
    if os.environ.get("POKEMONTCG_API_KEY"):
        headers["X-Api-Key"] = os.environ["POKEMONTCG_API_KEY"]

    for attempt in range(1, tries + 1):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=60) as response:
                return response.read()
        except urllib.error.HTTPError as e:
            if e.code == 404:
                raise NotFound(url) from e
            if e.code != 429 and e.code < 500 or attempt == tries:
                raise
            error = f"HTTP {e.code}"
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            if attempt == tries:
                raise
            error = str(getattr(e, "reason", e))
        wait = 2 ** attempt
        print(f"    Request failed ({error}), trying again in {wait} s...")
        time.sleep(wait)


def api(path, **params):
    return json.loads(fetch(f"{API}/{path}", params))


def all_sets():
    """Every set, newest first."""
    sets, page = [], 1
    while True:
        data = api("sets", orderBy="-releaseDate", page=page, pageSize=250)
        sets += data["data"]
        if not data["data"] or len(sets) >= data["totalCount"]:
            return sets
        page += 1


def cards_in_set(set_id):
    cards, page = [], 1
    while True:
        data = api("cards", q=f"set.id:{set_id}", page=page, pageSize=250,
                   select="id,name,number,images")
        cards += data["data"]
        if not data["data"] or len(cards) >= data["totalCount"]:
            return cards
        page += 1


# ── Choosing sets and files ─────────────────────────────────────────────────────────────────

def choose_sets(args):
    """The sets to download, as (id, name) pairs."""
    if args.sets:
        wanted = [s.lower() for s in args.sets]
        known = {s["id"].lower(): s for s in all_sets()}
        unknown = [s for s in wanted if s not in known]
        if unknown:
            sys.exit(f"Unknown set id(s): {', '.join(unknown)}. Use --list-sets to see them all.")
        chosen = [(known[s]["id"], known[s]["name"]) for s in wanted]
        newest = args.newest or 0
    else:
        chosen = [(BASE_SET_ID, BASE_SET_NAME)]
        newest = 3 if args.newest is None else args.newest

    if newest:
        excludes = [x.lower() for x in args.exclude]
        for s in all_sets():
            if newest == 0:
                break
            if any(s["id"] == c[0] for c in chosen):
                continue
            if any(x in s["name"].lower() for x in excludes):
                continue
            chosen.append((s["id"], s["name"]))
            newest -= 1
    return chosen


def safe_name(text):
    return re.sub(r"[^\w\-]+", "_", text).strip("_")


def number_key(number):
    # Card numbers can be "4", "TG01", "SV001"...
    digits = re.sub(r"\D", "", number)
    return re.sub(r"\d", "", number), int(digits) if digits else 0


def file_label(number):
    return f"{int(number):03d}" if number.isdigit() else safe_name(number)


def set_downloads(set_id, large):
    """(file name, url) for every card in a set."""
    try:
        cards = sorted(cards_in_set(set_id), key=lambda c: number_key(c["number"]))
        size = "large" if large else "small"
        names = [f"{file_label(c['number'])}_{safe_name(c['name'])}" for c in cards]
        # Variants can share a number and name; the card id tells them apart.
        return [(f"{name}_{safe_name(c['id'])}.png" if names.count(name) > 1 else f"{name}.png", c["images"][size])
                for name, c in zip(names, cards)]
    except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
        if set_id != BASE_SET_ID:
            raise
        print(f"    The API isn't answering ({e}), so getting Base Set straight from the image server.")
        suffix = "_hires" if large else ""
        return [(f"{n:03d}.png", BASE_IMAGE_URL.format(number=n, suffix=suffix)) for n in range(1, BASE_SET_SIZE + 1)]


# ── Downloading ─────────────────────────────────────────────────────────────────────────────

def download(url, path):
    # Written under another name first, so a stopped download never leaves a broken image
    # behind for Mosaic Generator to read.
    partial = path.with_name(path.name + ".part")
    partial.write_bytes(fetch(url, tries=3))
    partial.replace(path)


def download_set(downloads, folder):
    """Downloads what isn't there yet. Returns (downloaded, already there, failed)."""
    folder.mkdir(parents=True, exist_ok=True)
    missing = [(name, url) for name, url in downloads if not (folder / name).exists()]
    have = len(downloads) - len(missing)
    done, failed = 0, []

    if not missing:
        print(f"    All {len(downloads)} cards are already there.")
        return 0, have, []

    with ThreadPoolExecutor(WORKERS) as pool:
        jobs = {pool.submit(download, url, folder / name): name for name, url in missing}
        for job in as_completed(jobs):
            try:
                job.result()
            except Exception as e:
                failed.append(jobs[job])
                print(f"\r    Couldn't download {jobs[job]}: {e}")
            done += 1
            print(f"\r    {have + done}/{len(downloads)} cards", end="", flush=True)
    print()
    return done - len(failed), have, failed


# ── Main ────────────────────────────────────────────────────────────────────────────────────

def list_sets():
    sets = all_sets()
    print(f"{'Id':<14} {'Released':<11} {'Cards':>5}  Name")
    for s in sets:
        print(f"{s['id']:<14} {s['releaseDate']:<11} {s['total']:>5}  {s['name']}")


def main():
    # "é" and "©" would come out garbled when the output is redirected on Windows.
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(
        description="Download Pokémon TCG card images to use as a card folder for Mosaic Generator.")
    parser.add_argument("--sets", nargs="+", metavar="ID",
                        help="download exactly these sets (see --list-sets); without it: Base Set + the newest sets")
    parser.add_argument("--newest", type=int, metavar="N",
                        help="also download the N newest sets (default: 3, or 0 with --sets)")
    parser.add_argument("--exclude", nargs="*", default=["30th", "celebration"], metavar="WORD",
                        help="skip newest sets whose name contains one of these words (default: 30th celebration)")
    parser.add_argument("--large", action="store_true",
                        help="full-size scans (about 734 px wide) instead of small images (about 245 px wide, "
                             "enough for card widths up to about 250 px, and ~10x smaller)")
    parser.add_argument("--out", default="pokemon_cards", metavar="FOLDER",
                        help="where to save the cards (default: ./pokemon_cards)")
    parser.add_argument("--list-sets", action="store_true", help="show all sets and their ids, then stop")
    args = parser.parse_args()

    try:
        if args.list_sets:
            list_sets()
            return 0

        print("Card images come from the Pokémon TCG API (pokemontcg.io) and are © The Pokémon Company,")
        print("Nintendo, Creatures and GAME FREAK. They're for your own use: please don't share them.\n")

        try:
            sets = choose_sets(args)
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            if args.sets:
                sys.exit(f"Couldn't reach the Pokémon TCG API: {e}")
            print(f"Couldn't reach the Pokémon TCG API ({e}), so only Base Set will be downloaded.\n")
            sets = [(BASE_SET_ID, BASE_SET_NAME)]

        root = Path(args.out).resolve()
        downloaded = existing = 0
        failed = 0
        for set_id, name in sets:
            print(f"{name} ({set_id})")
            try:
                downloads = set_downloads(set_id, args.large)
            except Exception as e:
                print(f"    Skipped: couldn't get the card list ({e}).")
                failed += 1
                continue
            new, have, missing = download_set(downloads, root / f"{set_id}_{safe_name(name)}")
            downloaded += new
            existing += have
            failed += len(missing)

        print(f"\nDone: {downloaded} cards downloaded" + (f", {existing} were already there" if existing else "") + ".")
        print(f"Card folder: {root}")
        if failed:
            print(f"{failed} downloads failed. Run the script again to retry them.")
            return 1
        return 0
    except KeyboardInterrupt:
        print("\nStopped. Run the script again to continue where it left off.")
        return 130


if __name__ == "__main__":
    sys.exit(main())
