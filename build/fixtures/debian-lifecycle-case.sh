#!/bin/sh
set -eu
mkdir -p /run/systemd/system
scripts="$FIXTURE_REPO/packaging/debian"
if [ "$CASE" != inactive ]; then
    touch /tmp/case/state/sharpclaw-api.service.active /tmp/case/state/sharpclaw-gateway.service.active
fi
if [ "$CASE" = active-enabled ]; then
    touch /tmp/case/state/sharpclaw-api.service.enabled /tmp/case/state/sharpclaw-gateway.service.enabled
fi
if [ "$CASE" = stop-blocked ]; then
    export FAKE_POLICY_EXIT=101
    if /bin/sh "$scripts/prerm" upgrade; then echo 'Blocked stop incorrectly succeeded.' >&2; exit 1; fi
    test -f /tmp/case/state/sharpclaw-api.service.active
    test -f /run/sharpclaw-package-resume/sharpclaw-api
    /bin/sh "$scripts/postinst" abort-upgrade
    test ! -f /run/sharpclaw-package-resume/sharpclaw-api
    exit 0
fi
/bin/sh "$scripts/prerm" upgrade
test ! -f /tmp/case/state/sharpclaw-api.service.active
test ! -f /tmp/case/state/sharpclaw-gateway.service.active
case "$CASE" in
    masked) touch /tmp/case/state/sharpclaw-api.service.masked ;;
    policy-blocked) export FAKE_POLICY_EXIT=101 ;;
    policy-error) export FAKE_POLICY_EXIT=1 ;;
    start-failed) export FAKE_START_FAILURE=1 ;;
    start-noop) export FAKE_START_NOOP=1 ;;
    unrecorded-api) rm /run/sharpclaw-package-resume/sharpclaw-api ;;
esac
action=configure
if [ "$CASE" = abort-upgrade ]; then action=abort-upgrade; fi
case "$CASE" in
    masked|policy-blocked|policy-error|start-failed|start-noop|unrecorded-api)
        if /bin/sh "$scripts/postinst" "$action"; then echo 'Blocked restoration incorrectly succeeded.' >&2; exit 1; fi
        test -f /run/sharpclaw-package-resume/sharpclaw-gateway
        test ! -f /tmp/case/state/sharpclaw-gateway.service.active
        if [ "$CASE" != unrecorded-api ]; then test -f /run/sharpclaw-package-resume/sharpclaw-api; fi
        ;;
    inactive)
        /bin/sh "$scripts/postinst" "$action"
        test ! -f /tmp/case/state/sharpclaw-api.service.active
        test ! -f /tmp/case/state/sharpclaw-gateway.service.active
        ;;
    *)
        /bin/sh "$scripts/postinst" "$action"
        test -f /tmp/case/state/sharpclaw-api.service.active
        test -f /tmp/case/state/sharpclaw-gateway.service.active
        test ! -f /run/sharpclaw-package-resume/sharpclaw-api
        test ! -f /run/sharpclaw-package-resume/sharpclaw-gateway
        if [ "$CASE" = active-disabled ]; then
            test ! -f /tmp/case/state/sharpclaw-api.service.enabled
            test ! -f /tmp/case/state/sharpclaw-gateway.service.enabled
        fi
        ;;
esac
if grep -q '^enable' /tmp/case/commands.log; then echo 'Unexpected boot enablement.' >&2; exit 1; fi
if [ "$CASE" = inactive ] && grep -q '^start' /tmp/case/commands.log; then echo 'Fresh inactive service was started.' >&2; exit 1; fi
