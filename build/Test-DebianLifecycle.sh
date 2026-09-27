#!/bin/sh
# Execute real maintainer scripts ONLY inside a disposable user/filesystem namespace.
set -eu
repo=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)
command -v bwrap >/dev/null
test_root=$(mktemp -d /tmp/sharpclaw-debian-lifecycle.XXXXXXXX)
trap 'rm -r -- "$test_root"' EXIT HUP INT TERM
for scenario in active-enabled active-disabled inactive abort-upgrade masked policy-blocked \
    policy-error start-failed start-noop unrecorded-api stop-blocked; do
    case_root="$test_root/$scenario"
    mkdir -p "$case_root/bin" "$case_root/state"
    cp "$repo/build/fixtures/debian-systemctl.sh" "$case_root/bin/systemctl"
    for command in getent id install; do
        cp "$repo/build/fixtures/debian-account-tools.sh" "$case_root/bin/$command"
    done
    cp "$repo/build/fixtures/debian-policy.sh" "$case_root/policy-rc.d"
    chmod 0755 "$case_root/bin/"* "$case_root/policy-rc.d"
    # All host files are read-only. Every maintainer-script writable path is
    # tmpfs; fake systemctl controls only /tmp/case/state, never the host manager.
    bwrap --unshare-all --uid 0 --gid 0 --ro-bind / / --proc /proc --dev /dev \
        --tmpfs /run --tmpfs /var --tmpfs /etc --tmpfs /tmp --tmpfs /usr/sbin \
        --bind "$case_root" /tmp/case \
        --ro-bind "$case_root/policy-rc.d" /usr/sbin/policy-rc.d \
        --setenv PATH /tmp/case/bin:/usr/bin:/bin --setenv CASE "$scenario" \
        --setenv FIXTURE_REPO "$repo" --unsetenv DPKG_ROOT \
        /bin/sh "$repo/build/fixtures/debian-lifecycle-case.sh"
    echo "PASS $scenario"
done
