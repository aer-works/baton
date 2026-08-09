/// Ported from `Aer.Ui.Core.PermissionGrantWording.RoomShellGrantReaches()` — the honesty clause
/// under 0022's "any command in this room" rung: granting the room's shell standing is granting what
/// a shell reaches (files, network) regardless of which categories were otherwise withheld. One
/// wording home on desktop; this is the mobile mirror of that exact sentence so the two surfaces
/// can't drift on what the shell defeats.
library;

const String allowRoomShellGrantReaches = 'Allowing any command in this room grants the shell, and '
    'a shell command reaches read files, write files and network access anyway — those come with it.';
