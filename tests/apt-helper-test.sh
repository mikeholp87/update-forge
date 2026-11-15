#!/usr/bin/env bash
# Checks the argument rules of packaging/linux/updateforge-apt-helper without root: accepted input stops at the root check
# ("must run as root"), anything else is refused before it. apt-get is never started.
set -uo pipefail

helper="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/packaging/linux/updateforge-apt-helper"
[[ $(id -u) -ne 0 ]] || {
	echo "run as a normal user" >&2
	exit 2
}
failed=0

check() {
	local want=$1 got out
	shift
	out=$("$helper" "$@" 2>&1)
	if [[ $out == *"must run as root"* ]]; then got=accepted; else got=refused; fi
	if [[ $got != "$want" ]]; then
		echo "FAIL: want $want, got $got for: $*"
		failed=1
	fi
}

check accepted update
check accepted serve
check refused serve extra
check accepted install vim
check accepted install vim:amd64=2:9.1.2-1
check accepted upgrade libssl3t64:amd64=3.5.1-1+deb13u1
check refused upgrade vim
check refused update extra
check refused install -oAPT::Foo=1
check refused install ./evil.deb
check refused install vim/unstable
check refused install vim extra
check refused install $'vim\n-oFoo'
check refused install 'vim;id'
check refused install Vim
check refused purge vim
check refused

[[ $failed -eq 0 ]] && echo "apt helper: all checks passed"
exit $failed
