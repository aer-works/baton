/// The user-facing honesty clause for the shell-grant rung. `Aer.Ui.Core.PermissionGrantWording`
/// (desktop) is the one home that documents *why* it reads this way; the constant below is the
/// mobile copy of that exact string, kept character-identical so the two surfaces can't drift.
library;

const String allowRoomShellGrantReaches = 'Allowing any command in this room grants the shell, and '
    'a shell command reaches read files, write files and network access anyway — those come with it.';
