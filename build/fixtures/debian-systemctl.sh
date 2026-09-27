#!/bin/sh
set -eu
operation=''
units=''
for argument do
    case "$argument" in
        --*) ;;
        sharpclaw-*.service) units="$units $argument" ;;
        *) operation="$argument" ;;
    esac
done
echo "$operation$units" >> /tmp/case/commands.log
case "$operation" in
    daemon-reload) exit 0 ;;
    is-active)
        for unit in $units; do [ -f "/tmp/case/state/$unit.active" ] || exit 3; done
        ;;
    is-enabled)
        for unit in $units; do
            if [ -f "/tmp/case/state/$unit.enabled" ]; then echo enabled; else echo disabled; exit 1; fi
        done
        ;;
    show)
        for unit in $units; do
            if [ -f "/tmp/case/state/$unit.masked" ]; then echo masked; else echo loaded; fi
        done
        ;;
    stop)
        for unit in $units; do rm -f "/tmp/case/state/$unit.active"; done
        ;;
    start)
        if [ "${FAKE_START_FAILURE:-0}" -ne 0 ]; then exit 1; fi
        if [ "${FAKE_START_NOOP:-0}" -eq 0 ]; then
            for unit in $units; do touch "/tmp/case/state/$unit.active"; done
        fi
        ;;
    *) echo "Unexpected systemctl operation $operation" >&2; exit 1 ;;
esac
