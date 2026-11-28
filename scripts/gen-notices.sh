#!/usr/bin/env bash
# Writes THIRD-PARTY-NOTICES.txt for a published Linux build: every NuGet package the app ships, with its licence and copyright,
# followed by the licence texts that must travel with the binaries (MIT, BSD-3-Clause for Skia, the font licence, Apache-2.0 pointer).
#   scripts/gen-notices.sh <publish-dir> <output-file>
# Needs the NuGet cache from the restore that preceded the publish (the same machine, so it is always there).
set -euo pipefail

publish="${1:?usage: gen-notices.sh <publish-dir> <output-file>}"
out="${2:?usage: gen-notices.sh <publish-dir> <output-file>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deps="$publish/updateforge.deps.json"
nuget="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
[[ -f $deps ]] || {
	echo "gen-notices: $deps not found" >&2
	exit 2
}

# Decodes the XML entities nuspec files use and joins the value onto one line.
xml_text() { tr '\n' ' ' | sed -e 's/&amp;/\&/g' -e 's/&lt;/</g' -e 's/&gt;/>/g' -e 's/&quot;/"/g' -e 's/  */ /g' -e 's/^ //' -e 's/ $//'; }

{
	echo "UpdateForge third-party notices"
	echo "================================"
	echo
	echo "UpdateForge itself is MIT licensed (see LICENSE). This build bundles the components below. Each one is used under the licence shown."
	echo
	echo "The .NET runtime (self-contained)"
	echo "  Licence: MIT. Copyright (c) .NET Foundation and Contributors."
	echo "  https://github.com/dotnet/runtime/blob/main/LICENSE.TXT"
	echo "  https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT"
	echo
	echo "NuGet packages"
	echo "--------------"
	jq -r '.libraries | to_entries[] | select(.value.type == "package") | .key' "$deps" | sort -f | while IFS=/ read -r id ver; do
		lower="${id,,}"
		spec="$nuget/$lower/$ver/$lower.nuspec"
		license="see https://www.nuget.org/packages/$id/$ver"
		copyright=""
		if [[ -f $spec ]]; then
			l="$(sed -n 's|.*<license type="expression">\([^<]*\)</license>.*|\1|p' <(tr '\n' ' ' <"$spec") | xml_text)"
			[[ -z $l ]] || license="$l"
			copyright="$(sed -n 's|.*<copyright>\([^<]*\)</copyright>.*|\1|p' <(tr '\n' ' ' <"$spec") | xml_text)"
		fi
		echo "$id $ver"
		echo "  Licence: $license"
		[[ -z $copyright ]] || echo "  $copyright"
	done
	echo
	echo "Native code inside those packages"
	echo "---------------------------------"
	echo "SkiaSharp.NativeAssets.Linux and HarfBuzzSharp.NativeAssets.Linux contain Skia (BSD-3-Clause, text below), FreeType, HarfBuzz,"
	echo "libpng, zlib and expat under their own permissive licences: https://github.com/mono/SkiaSharp/blob/main/THIRD-PARTY-NOTICES.md"
	echo "SQLitePCLRaw.lib.e_sqlite3 contains SQLite, which is in the public domain: https://www.sqlite.org/copyright.html"
	echo
	echo "Fonts"
	echo "-----"
	echo "IBM Plex Sans and IBM Plex Mono are used under the SIL Open Font License 1.1:"
	echo
	cat "$root/src/UpdateForge.Desktop.Avalonia/Assets/Fonts/OFL.txt"
	echo
	echo "Licence texts"
	echo "-------------"
	echo
	echo "MIT License (applies to every package above marked MIT, with that package's copyright line)"
	echo
	echo "  Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation"
	echo "  files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy,"
	echo "  modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software"
	echo "  is furnished to do so, subject to the following conditions:"
	echo
	echo "  The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software."
	echo
	echo "  THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES"
	echo "  OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE"
	echo "  LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR"
	echo "  IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."
	echo
	echo "Apache License 2.0 (Serilog, SQLitePCLRaw): https://www.apache.org/licenses/LICENSE-2.0 (on Debian: /usr/share/common-licenses/Apache-2.0)"
	echo
	echo "BSD 3-Clause License (Skia)"
	echo
	echo "  Copyright (c) 2011 Google Inc. All rights reserved."
	echo
	echo "  Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following"
	echo "  conditions are met:"
	echo "   * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer."
	echo "   * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following"
	echo "     disclaimer in the documentation and/or other materials provided with the distribution."
	echo "   * Neither the name of Google Inc. nor the names of its contributors may be used to endorse or promote products derived from"
	echo "     this software without specific prior written permission."
	echo
	echo "  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS \"AS IS\" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING,"
	echo "  BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT"
	echo "  SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL"
	echo "  DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS"
	echo "  INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE"
	echo "  OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE."
} >"$out"
echo "Wrote $out ($(grep -c ' Licence: ' "$out") components)"
