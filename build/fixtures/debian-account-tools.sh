#!/bin/sh
set -eu
case "${0##*/}" in
    getent) echo 'sharpclaw:x:995:995::/var/lib/sharpclaw:/usr/sbin/nologin' ;;
    id) echo 995 ;;
    install)
        for argument do
            case "$argument" in /run/*|/var/*) mkdir -p -- "$argument" ;; esac
        done
        ;;
    *) exit 1 ;;
esac
