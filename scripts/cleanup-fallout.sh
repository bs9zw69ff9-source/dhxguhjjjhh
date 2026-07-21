#!/usr/bin/env bash
# Remove the OLD Fallout faction files (spawn + rank .txt) left in FactionRoles
# after switching the whitelists to Gambino / Colombo / NYPD. It deletes ONLY the
# exact old filenames listed below - current rosters, the donator file, faction.txt,
# and anything else are never touched. Run it once per host; it hits every install
# you point it at (files are mirrored across installs, so clean them all).
#
#   usage:  bash scripts/cleanup-fallout.sh <FactionRoles-dir> [more dirs...]
#   e.g.    bash scripts/cleanup-fallout.sh \
#             /root/pavlovserver/Pavlov/Saved/Config/ModSave/FactionRoles
#
# With no arguments it derives the dirs from PAVLOV_BASES (or PAVLOV_BASE_1) in the
# environment, appending Pavlov/Saved/Config/ModSave/FactionRoles to each base.
set -euo pipefail

OLD_FILES=(
  # NCR
  ncrspawn.txt ncrprivate.txt ncrcorporal.txt ncrsergeant.txt ncrmedix.txt ncrheavy.txt ncrpowerarmor.txt ncrmp.txt ncrranger.txt ncrlieutenant.txt ncrofficer.txt
  # Legion
  legionspawn.txt legionrecruit.txt legionlegionnaire.txt legionexplorer.txt legionslavemaster.txt legionprimelegionary.txt legionveteranlegionnaire.txt legionvexalarius.txt legioncenturion.txt legionassasin.txt legionpraetorian.txt legionlegate.txt
  # Enclave
  enclavespawn.txt enclavebrigadeless.txt enclavetruthandjustice.txt enclaveresearch.txt enclave56thriflebrigade.txt enclaverecon.txt enclavedemolition.txt enclavemechanized.txt enclavehellfire.txt enclaveofficer.txt
  # Khans (khanspawn is an old alias of khansspawn)
  khansspawn.txt khanspawn.txt khansprospect.txt khansenforcer.txt khansbeserker.txt khansseargent.txt khansskirmisher.txt khansmarksmen.txt
  # Brotherhood of Steel
  bosspawn.txt bosinitiate.txt bosknight.txt bospaladin.txt boselder.txt
  # Kings
  kingsspawn.txt kingsprospect.txt kingssilverace.txt kingsguard.txt kingshighroller.txt kingscrown.txt kingstheking.txt
  # Super Mutants
  supermutantspawn.txt supermutantsuicider.txt supermutantinfantry.txt supermutantsergeant.txt supermutantnightkin.txt supermutantbombardier.txt supermutantbehemoth.txt
  # Followers
  followersspawn.txt followersfollower.txt followersguard.txt followersrecruit.txt followersscholar.txt followersdirector.txt
)

REL="Pavlov/Saved/Config/ModSave/FactionRoles"
dirs=()
if [ "$#" -gt 0 ]; then
  dirs=("$@")
else
  bases="${PAVLOV_BASES:-${PAVLOV_BASE_1:-}}"
  if [ -z "$bases" ]; then
    echo "No dirs given and PAVLOV_BASES / PAVLOV_BASE_1 are not set." >&2
    echo "usage: bash scripts/cleanup-fallout.sh <FactionRoles-dir> [more dirs...]" >&2
    exit 1
  fi
  IFS=',:' read -ra arr <<< "$bases"
  for b in "${arr[@]}"; do b="${b%/}"; [ -n "$b" ] && dirs+=("$b/$REL"); done
fi

total=0
for dir in "${dirs[@]}"; do
  if [ ! -d "$dir" ]; then echo "skip (not a directory): $dir" >&2; continue; fi
  n=0
  for f in "${OLD_FILES[@]}"; do
    if [ -e "$dir/$f" ]; then rm -f -- "$dir/$f" && n=$((n + 1)); fi
  done
  echo "$dir -> removed $n old Fallout file(s)"
  total=$((total + n))
done
echo "done: removed $total file(s) across ${#dirs[@]} directory(ies)"
