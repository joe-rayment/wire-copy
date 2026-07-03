"""Shared WireCopy keybinding constants for the live tmux-driven gate scripts.

Single source of truth for the app-specific CHORDS the live gates drive, so a
binding change (like the Ctrl+L -> 'g l' move for the AI layout wizard) is edited
here once instead of in 7 hard-coded sites across 5 scripts.

This mirrors the C# key registry
(src/WireCopy.Infrastructure/Browser/UI/KeyRegistry.cs) — the in-app source of
truth. Keep the two in sync when a binding moves.

Terminal-generic keys (Enter, Escape, j, k, Space, Down) are NOT centralised
here: they are tmux key names, not WireCopy bindings, and never go stale.

Each constant is a tuple of tmux key names, fed to TermTest.send_keys(*CHORD).
The convenience helpers drive a chord against a TermTest instance.
"""

# --- Chords (multi-key sequences) --------------------------------------------

# 'g' then 'l' opens the AI layout wizard (was Ctrl+L before workspace-1dmr).
CHOOSE_LAYOUT = ("g", "l")

# 'g' then 'g' jumps to the top.
GO_TO_TOP = ("g", "g")

# --- Single app-specific keys (tmux names) -----------------------------------

DOCK_BROWSER = "|"        # ToggleBrowserDock
ADOPT_LENS_PAGE = "y"     # AdoptLensPage
UNDO = "z"                # Undo (e.g. undo a wizard refine)
TUNE_ARTICLE_LAYOUT = "E"  # TuneArticleLayout (Shift+E)
FORCE_REFRESH = "R"       # ForceRefresh (Shift+R)
INTERACTIVE_REFRESH = "I"  # InteractiveRefresh (Shift+I)


def choose_layout(t, delay: float = 0.15):
    """Drive the 'g l' AI-layout-wizard chord on TermTest ``t``."""
    t.send_keys(*CHOOSE_LAYOUT, delay=delay)
