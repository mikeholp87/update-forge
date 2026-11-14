namespace UpdateForge.Core.Recommendations;

/// <summary>One way to get a package. Candidates are listed in order of preference; the first whose manager is usable wins.</summary>
internal sealed record Candidate(string ProviderId, string PackageId, string? Source = null);

/// <param name="When">
/// Lower-case names or package ids; any one installed triggers the rule. <c>@apt</c> means "anything installed through apt".
/// Matching also uses the last segment of a Flatpak id, so <c>steam</c> matches <c>com.valvesoftware.Steam</c>.
/// </param>
/// <param name="Broad">True for general suggestions that fit nearly every system. They rank below specific ones and show less "because" detail.</param>
/// <param name="Niches">Interest areas this belongs to. A rule with no <c>When</c> is only suggested when the user's interest in one of its niches is strong enough.</param>
/// <param name="Aliases">Extra lower-case names that mean "already installed" and that classify an installed program into the niches.</param>
internal sealed record Rule(string Key, string Title, string Category, string Why, string? Tip, string[] When, Candidate[] Candidates, int Priority, bool Broad = false,
    string[]? Niches = null, string[]? Aliases = null);

/// <summary>
/// The curated suggestions. A small, reviewed list on purpose: nothing here is fetched, ranked by popularity or influenced by anyone,
/// and every entry says why it is suggested. Add a rule by appending to <see cref="Rules"/>.
/// </summary>
internal static class RecommendationCatalog
{
    private static Candidate Apt(string id) => new("apt", id);
    private static Candidate Brew(string id) => new("brew", id);
    private static Candidate Cargo(string id) => new("cargo", id);
    private static Candidate Pipx(string id) => new("pipx", id);
    private static Candidate Flathub(string id) => new("flatpak", id, "flathub");
    private static Candidate Flatpak(string id) => Flathub(id);

    public static readonly IReadOnlyList<Rule> Rules =
    [
        // Keeping the system updated and safe
        new("needrestart", "needrestart", "Updates", "Shows which services and programs still run old code after an upgrade.",
            "Run `sudo needrestart` after Update all to restart only what changed instead of rebooting.", ["@apt"], [Apt("needrestart")], 90, Niches: ["sysadmin"]),
        new("fwupd", "fwupd", "Updates", "Updates firmware (laptop, SSD, dock, keyboard) from your vendor through LVFS, which package managers do not cover.",
            "`fwupdmgr get-updates` lists what is available for this machine.", ["@apt"], [Apt("fwupd")], 85, Niches: ["sysadmin"]),
        new("unattended-upgrades", "unattended-upgrades", "Updates", "Installs security updates in the background without any prompt.",
            "Pair it with Manual mode here: security fixes arrive on their own, and you choose when everything else updates.", ["@apt"], [Apt("unattended-upgrades")], 60, Niches: ["security", "sysadmin"]),
        new("timeshift", "Timeshift", "Updates", "Takes a snapshot before big upgrades so you can roll back.",
            "Take one before a large dist-upgrade or before switching Debian releases.", ["@apt"], [Apt("timeshift")], 70, Niches: ["backup"]),
        new("ncdu", "ncdu", "Updates", "Shows what is using your disk, so large updates do not fail for lack of space.",
            "`ncdu /` browses disk use; press `d` to delete something.", ["@apt"], [Apt("ncdu")], 35, Broad: true, Niches: ["sysadmin", "terminal"]),

        // Flatpak
        new("flatseal", "Flatseal", "Flatpak", "Lets you review and tighten what each Flatpak app can access: files, network, devices.",
            "Start with apps that have 'Filesystem: All user files' and turn off what they do not need.", ["@flatpak"], [Flathub("com.github.tchx84.Flatseal")], 75, Niches: ["security"]),
        new("warehouse", "Warehouse", "Flatpak", "Manage Flatpak apps, remotes and leftovers from one window.",
            "`flatpak uninstall --unused` removes runtimes that no app needs any more and can free gigabytes.", ["@flatpak"], [Flathub("io.github.flattool.Warehouse")], 55),

        // Gaming
        new("protonup-qt", "ProtonUp-Qt", "Gaming", "Installs community Proton builds that fix games the official Proton breaks.",
            "Pick the newest GE-Proton, restart Steam, then choose it per game under Properties > Compatibility.", ["steam", "lutris", "heroic"], [Flathub("net.davidotek.pupgui2")], 70, Niches: ["gaming"]),
        new("gamemode", "GameMode", "Gaming", "Applies CPU and GPU performance settings only while a game is running.",
            "Add `gamemoderun %command%` to a game's launch options in Steam.", ["steam", "lutris", "heroic"], [Apt("gamemode")], 60, Niches: ["gaming"]),
        new("mangohud", "MangoHud", "Gaming", "An in-game overlay with FPS, temperatures and load.",
            "Add `mangohud %command%` to a game's launch options, and press Shift+F12 to hide it.", ["steam", "lutris", "heroic"], [Apt("mangohud")], 55, Niches: ["gaming"]),

        // Monitoring and files
        new("btop", "btop", "Terminal", "A friendlier htop, with disk, network and process graphs.",
            "Press `m` for the menu and `esc` to leave.", ["htop", "top"], [Apt("btop"), Brew("btop")], 65, Niches: ["terminal", "sysadmin"]),

        // Development
        new("git-delta", "delta", "Development", "Readable, syntax-highlighted diffs in git.",
            "`git config --global core.pager delta` turns it on everywhere.", ["git"], [Apt("git-delta"), Brew("git-delta"), Cargo("git-delta")], 60, Niches: ["development"]),
        new("lazygit", "lazygit", "Development", "A terminal interface for staging, rebasing and managing branches.",
            "Press `?` inside it for every key binding.", ["git"], [Apt("lazygit"), Brew("lazygit")], 55, Niches: ["development"]),
        new("pipx", "pipx", "Development", "Installs Python command-line apps in their own environments so they cannot break each other.",
            "Prefer `pipx install` over `pip install --user`. UpdateForge updates pipx apps too.", ["python3", "python"], [Apt("pipx"), Brew("pipx")], 70, Niches: ["development"]),
        new("pnpm", "pnpm", "Development", "A faster, disk-saving alternative to npm that shares packages between projects.",
            "UpdateForge updates pnpm's global packages separately from npm's.", ["@npm", "nodejs", "node"], [new("npm", "pnpm")], 45, Niches: ["development"]),
        new("jq", "jq", "Development", "Query and reshape JSON on the command line, which most web tools produce.",
            "`curl -s URL | jq .` pretty-prints a response.", ["curl", "wget", "nodejs", "python3"], [Apt("jq"), Brew("jq")], 50, Niches: ["development"]),
        new("tmux", "tmux", "Development", "Keeps terminal sessions alive when an SSH connection drops.",
            "`tmux new -s work` to start, `tmux attach -t work` to come back.", ["openssh-client", "openssh-server"], [Apt("tmux"), Brew("tmux")], 40, Niches: ["terminal", "sysadmin"]),
        new("shellcheck", "ShellCheck", "Development", "Catches common mistakes in shell scripts before they bite.",
            "Run `shellcheck script.sh` before committing a script.", ["bash", "zsh"], [Apt("shellcheck"), Brew("shellcheck")], 40, Broad: true, Niches: ["development"]),

        // Media
        new("ffmpeg", "FFmpeg", "Media", "Converts and edits audio and video from the command line; many media apps rely on it.",
            "`ffmpeg -i in.mkv out.mp4` converts between formats.", ["vlc", "mpv", "handbrake", "obs-studio", "kdenlive"], [Apt("ffmpeg"), Brew("ffmpeg")], 50, Niches: ["media"]),
        new("yt-dlp", "yt-dlp", "Media", "Downloads video and audio from many sites.",
            "Sites change often. The pipx version stays current and UpdateForge keeps it updated.", ["vlc", "mpv"], [Pipx("yt-dlp"), Apt("yt-dlp"), Brew("yt-dlp")], 45, Niches: ["media"]),

        // General command-line quality of life
        new("ripgrep", "ripgrep", "Terminal", "A much faster grep that respects .gitignore.",
            "`rg pattern` searches the current folder recursively.", ["bash", "zsh", "fish"], [Apt("ripgrep"), Brew("ripgrep"), Cargo("ripgrep")], 45, Broad: true, Niches: ["terminal"]),
        new("fzf", "fzf", "Terminal", "A fuzzy finder: turns Ctrl+R into a searchable history and finds files by typing a few letters.",
            "Install it, then add its shell integration from `/usr/share/doc/fzf/examples`.", ["bash", "zsh", "fish"], [Apt("fzf"), Brew("fzf")], 45, Broad: true, Niches: ["terminal"]),
        new("fd-find", "fd", "Terminal", "A simpler, faster find.",
            "On Debian the command is `fdfind`; add `alias fd=fdfind` to your shell.", ["bash", "zsh", "fish"], [Apt("fd-find"), Brew("fd"), Cargo("fd-find")], 40, Broad: true, Niches: ["terminal"]),
        new("bat", "bat", "Terminal", "cat with syntax highlighting, line numbers and paging.",
            "On Debian the command is `batcat`; add `alias bat=batcat`.", ["bash", "zsh", "fish"], [Apt("bat"), Brew("bat"), Cargo("bat")], 40, Broad: true, Niches: ["terminal"]),
        new("zoxide", "zoxide", "Terminal", "A smarter cd that learns the folders you use most.",
            "Add `eval \"$(zoxide init bash)\"` to your shell profile, then `z proj` jumps to ~/projects.", ["bash", "zsh", "fish"], [Apt("zoxide"), Brew("zoxide"), Cargo("zoxide")], 35, Broad: true, Niches: ["terminal"]),
        new("eza", "eza", "Terminal", "A modern ls with git status, colours and a tree view.",
            "`eza -l --git` is a good everyday default.", ["bash", "zsh", "fish"], [Apt("eza"), Brew("eza"), Cargo("eza")], 30, Broad: true, Niches: ["terminal"]),

        // Niche suggestions: only offered once the user's interest in the niche is clear (installed apps, or what they install over time).
        // Gaming
        Pick("lutris", "Lutris", "gaming", "Runs and organises games from many stores and emulators in one library.", "Use its installers to set up GOG, Epic and Battle.net games with Wine already configured.", 60, Flatpak("net.lutris.Lutris")),
        Pick("heroic", "Heroic Games Launcher", "gaming", "An open launcher for your Epic, GOG and Amazon games.", null, 60, Flatpak("com.heroicgameslauncher.hgl")),
        Pick("bottles", "Bottles", "gaming", "Keeps each Windows program or game in its own Wine environment.", "One bottle per game stops one fix from breaking another.", 50, Flatpak("com.usebottles.bottles")),
        Pick("prism-launcher", "Prism Launcher", "gaming", "Manages many Minecraft installs, versions and mods side by side.", null, 45, Flatpak("org.prismlauncher.PrismLauncher")),
        Pick("retroarch", "RetroArch", "gaming", "One front end for most classic console emulators.", null, 45, Apt("retroarch")),
        Pick("dolphin-emu", "Dolphin", "gaming", "A GameCube and Wii emulator.", null, 40, Flatpak("org.DolphinEmu.dolphin-emu")),
        Pick("goverlay", "GOverlay", "gaming", "A window to configure MangoHud and other game overlays.", null, 40, Apt("goverlay")),

        // Video and audio
        Pick("obs-studio", "OBS Studio", "media", "Records the screen and streams with scenes, sources and filters.", null, 60, Apt("obs-studio")),
        Pick("kdenlive", "Kdenlive", "media", "A full non-linear video editor.", null, 55, Apt("kdenlive")),
        Pick("shotcut", "Shotcut", "media", "A lighter video editor with many formats and filters.", null, 40, Apt("shotcut")),
        Pick("handbrake", "HandBrake", "media", "Converts video between formats and shrinks large files.", "The Fast 1080p30 preset is a good default for sharing.", 55, Apt("handbrake"), Flatpak("fr.handbrake.ghb")),
        Pick("mkvtoolnix", "MKVToolNix", "media", "Cuts, joins and edits the tracks inside MKV files without re-encoding.", null, 40, Apt("mkvtoolnix-gui")),
        Pick("mpv", "mpv", "media", "A minimal, scriptable video player that plays almost anything.", null, 45, Apt("mpv")),

        // Graphics and photos
        Pick("gimp", "GIMP", "creative", "The standard open image editor for photos and graphics.", null, 60, Apt("gimp"), Flatpak("org.gimp.GIMP")),
        Pick("krita", "Krita", "creative", "A painting and illustration program built for digital artists.", null, 60, Apt("krita"), Flatpak("org.kde.krita")),
        Pick("inkscape", "Inkscape", "creative", "Draws vector graphics: logos, icons and diagrams that scale cleanly.", null, 55, Apt("inkscape"), Flatpak("org.inkscape.Inkscape")),
        Pick("darktable", "darktable", "creative", "Develops RAW photos non-destructively.", null, 55, Apt("darktable")),
        Pick("rawtherapee", "RawTherapee", "creative", "A RAW photo developer with detailed colour and detail controls.", null, 45, Apt("rawtherapee")),
        Pick("blender", "Blender", "creative", "3D modelling, animation and rendering in one program.", null, 55, Apt("blender")),
        Pick("scribus", "Scribus", "creative", "Lays out print-ready documents, magazines and posters.", null, 35, Apt("scribus")),

        // Music making
        Pick("ardour", "Ardour", "music", "A digital audio workstation for recording and mixing.", null, 55, Apt("ardour")),
        Pick("audacity", "Audacity", "music", "Records and edits audio, and cleans up noise.", null, 55, Apt("audacity")),
        Pick("easyeffects", "EasyEffects", "music", "System-wide audio effects: equaliser, bass and noise reduction.", null, 50, Flatpak("com.github.wwmm.easyeffects")),
        Pick("lmms", "LMMS", "music", "Make beats and songs with a pattern-based sequencer.", null, 40, Flatpak("io.lmms.LMMS")),
        Pick("strawberry", "Strawberry", "music", "A music player built for large local libraries.", null, 40, Apt("strawberry")),
        Pick("picard", "MusicBrainz Picard", "music", "Tags and organises your music files from a public database.", null, 40, Apt("picard")),

        // Development
        Pick("neovim", "Neovim", "development", "A modern, extensible terminal editor.", "Start with `:Tutor`, then add plugins slowly.", 55, Apt("neovim"), Brew("neovim")),
        Pick("httpie", "HTTPie", "development", "A friendlier curl for calling and testing APIs.", "`http GET example.com/api` prints formatted, coloured JSON.", 50, Apt("httpie"), Brew("httpie")),
        Pick("sqlitebrowser", "DB Browser for SQLite", "development", "Browse and edit SQLite databases in a window.", null, 45, Apt("sqlitebrowser")),
        Pick("direnv", "direnv", "development", "Loads a project's environment variables when you enter its folder.", "Put them in `.envrc` and run `direnv allow` once.", 45, Apt("direnv"), Brew("direnv")),

        // Servers and networking
        Pick("nmap", "nmap", "sysadmin", "Finds devices and open ports on a network.", "Only scan networks you own or have permission to test.", 50, Apt("nmap")),
        Pick("mtr", "mtr", "sysadmin", "Traceroute and ping combined, to find where a connection drops.", "`mtr example.com` shows live loss per hop.", 50, Apt("mtr-tiny")),
        Pick("iperf3", "iperf3", "sysadmin", "Measures real network throughput between two machines.", null, 40, Apt("iperf3")),
        Pick("wireshark", "Wireshark", "sysadmin", "Captures and inspects network traffic packet by packet.", null, 45, Apt("wireshark")),
        Pick("rsync", "rsync", "sysadmin", "Copies and syncs files efficiently, locally or over SSH.", "`rsync -avh --progress src/ dest/` is the usual starting point.", 45, Apt("rsync")),

        // Privacy and security
        Pick("keepassxc", "KeePassXC", "security", "A password manager that keeps your vault in a local file.", "Use a long passphrase and keep a backup of the vault file.", 65, Apt("keepassxc"), Flatpak("org.keepassxc.KeePassXC")),
        Pick("mat2", "mat2", "security", "Removes hidden metadata (location, author, camera) from files before you share them.", null, 45, Apt("mat2")),
        Pick("bleachbit", "BleachBit", "security", "Clears caches and temporary files and frees disk space.", null, 40, Apt("bleachbit")),
        Pick("ufw", "UFW", "security", "A simple firewall front end.", "`sudo ufw default deny incoming && sudo ufw enable` is a safe desktop default.", 55, Apt("ufw")),
        Pick("wireguard", "WireGuard tools", "security", "A fast, modern VPN you can set up in a few lines.", null, 40, Apt("wireguard-tools")),
        Pick("clamav", "ClamAV", "security", "An open virus scanner, useful for files you pass on to others.", null, 35, Apt("clamav")),

        // Backups
        Pick("restic", "restic", "backup", "Encrypted, deduplicated backups to a drive or cloud storage.", "Test a restore once; an untested backup is only a hope.", 60, Apt("restic")),
        Pick("borgbackup", "BorgBackup", "backup", "Fast, space-saving backups that deduplicate and encrypt.", null, 55, Apt("borgbackup")),
        Pick("vorta", "Vorta", "backup", "A friendly window for BorgBackup, with schedules.", null, 50, Flatpak("com.borgbase.Vorta")),
        Pick("deja-dup", "Déjà Dup", "backup", "Simple scheduled backups of your home folder.", null, 50, Flatpak("org.gnome.DejaDup")),

        // Writing and research
        Pick("obsidian", "Obsidian", "office", "Notes as plain Markdown files, linked together.", null, 55, Flatpak("md.obsidian.Obsidian")),
        Pick("zotero", "Zotero", "office", "Collects sources and builds citations and bibliographies.", null, 55, Flatpak("org.zotero.Zotero")),
        Pick("pandoc", "pandoc", "office", "Converts documents between Markdown, Word, PDF and more.", "`pandoc notes.md -o notes.pdf` makes a PDF.", 50, Apt("pandoc"), Brew("pandoc")),
        Pick("calibre", "calibre", "office", "Manages and converts your e-book library.", null, 45, Apt("calibre")),
        Pick("libreoffice", "LibreOffice", "office", "A full office suite: documents, spreadsheets and slides.", null, 45, Apt("libreoffice")),

        // Containers and virtual machines
        Pick("podman", "Podman", "containers", "Runs containers without a root daemon.", null, 55, Apt("podman"), Brew("podman")),
        Pick("distrobox", "Distrobox", "containers", "Runs another Linux distribution in a terminal, sharing your home folder.", "Handy for software that is not packaged for your release.", 55, Apt("distrobox"), Brew("distrobox")),
        Pick("virt-manager", "Virtual Machine Manager", "containers", "Creates and runs virtual machines with KVM.", null, 50, Apt("virt-manager")),
        Pick("docker", "Docker", "containers", "Builds and runs containers; most projects ship a Dockerfile.", "After installing, add yourself to the docker group so commands do not need sudo.", 50, Apt("docker.io")),
    ];

    private static Rule Pick(string key, string title, string niche, string why, string? tip, int priority, params Candidate[] candidates) =>
        new(key, title, Niches.Title(niche), why, tip, [], candidates, priority, Niches: [niche]);

    /// <summary>
    /// Programs that show an interest even though nothing is suggested for them (or that are already installed). Lower-case names,
    /// matched against a program's name, package id and a Flatpak id's last segment.
    /// </summary>
    private static readonly Dictionary<string, string[]> Signals = new(StringComparer.Ordinal)
    {
        ["gaming"] = ["steam", "lutris", "heroic", "bottles", "retroarch", "prismlauncher", "dolphin-emu", "pcsx2", "rpcs3", "wine", "wine64", "winetricks", "gamescope", "minecraft-launcher", "goverlay", "hgl"],
        ["media"] = ["vlc", "mpv", "ffmpeg", "handbrake", "obs-studio", "kdenlive", "shotcut", "mkvtoolnix", "mkvtoolnix-gui", "yt-dlp", "kodi", "jellyfin", "celluloid", "openshot", "totem"],
        ["creative"] = ["gimp", "inkscape", "krita", "darktable", "rawtherapee", "blender", "scribus", "digikam", "pinta", "freecad", "openscad"],
        ["music"] = ["audacity", "ardour", "lmms", "strawberry", "rhythmbox", "easyeffects", "musescore", "mixxx", "tenacity", "picard"],
        ["development"] = ["git", "gh", "code", "vscode", "neovim", "emacs", "nodejs", "npm", "cargo", "rustc", "golang-go", "build-essential", "cmake", "clang", "postman", "insomnia", "httpie", "sqlitebrowser", "dotnet-sdk-10.0"],
        ["terminal"] = ["htop", "btop", "tmux", "zsh", "fish", "ripgrep", "fzf", "fd-find", "bat", "zoxide", "eza", "tldr", "neofetch", "fastfetch", "starship", "alacritty", "kitty", "wezterm", "foot", "tilix", "terminator"],
        ["sysadmin"] = ["nmap", "mtr-tiny", "iperf3", "wireshark", "tcpdump", "openssh-server", "ansible", "fail2ban", "rsync", "cockpit", "nginx", "apache2", "postgresql", "mariadb-server", "smartmontools", "tailscale", "zfsutils-linux"],
        ["security"] = ["keepassxc", "mat2", "bleachbit", "clamav", "ufw", "wireguard-tools", "openvpn", "tor", "veracrypt", "bitwarden", "protonvpn", "gufw"],
        ["backup"] = ["timeshift", "restic", "borgbackup", "vorta", "deja-dup", "rclone", "syncthing", "backintime-qt", "grsync"],
        ["office"] = ["libreoffice", "libreoffice-writer", "obsidian", "zotero", "calibre", "pandoc", "texstudio", "okular", "logseq", "joplin"],
        ["containers"] = ["docker.io", "docker-ce", "podman", "distrobox", "virt-manager", "qemu-system-x86", "libvirt-daemon", "virtualbox", "kubectl", "minikube", "toolbox"],
    };

    /// <summary>Programs installed through these managers count toward a niche: developer tooling says "development" even if the catalog has never heard of the package.</summary>
    public static readonly IReadOnlyDictionary<string, string> ProviderNiche = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["npm"] = "development", ["pnpm"] = "development", ["bun"] = "development", ["yarn"] = "development", ["cargo"] = "development", ["pipx"] = "development",
    };

    private static readonly Lazy<Dictionary<string, HashSet<string>>> ByName = new(() =>
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string name, string niche)
        {
            if (!map.TryGetValue(name, out var set)) map[name] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(niche);
        }
        foreach (var (niche, names) in Signals) foreach (var n in names) Add(n, niche);
        foreach (var rule in Rules.Where(r => r.Niches is { Length: > 0 }))
            foreach (var niche in rule.Niches!)
            {
                Add(rule.Title.ToLowerInvariant(), niche);
                foreach (var c in rule.Candidates) Add(c.PackageId.ToLowerInvariant(), niche);
                foreach (var a in rule.Aliases ?? []) Add(a, niche);
            }
        return map;
    });

    /// <summary>The niches any of these installed-program names belong to.</summary>
    public static IEnumerable<string> NichesByName(IEnumerable<string> names) =>
        names.Select(n => ByName.Value.GetValueOrDefault(n)).Where(set => set is not null).SelectMany(set => set!).Distinct(StringComparer.Ordinal);
}
