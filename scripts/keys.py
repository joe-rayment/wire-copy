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

# --- Label mode (workspace-t1ok.4, SetupWizard.RunLabelModeAsync) -------------
# Inside the wizard's "Mark the links" card only; matched by raw key char.

LABEL_ARTICLE = "a"       # article — press order becomes the story order; again = clear
LABEL_AD = "x"            # ad — exclude + extrapolate its slot
LABEL_MENU = "m"          # site chrome to keep — routes under the collapsed More menu
LABEL_IGNORE = "i"        # site chrome to hide entirely
LABEL_CLEAR = "u"         # clear the row's label
LABEL_TOGGLE_ALL = "Tab"  # flip current-layout <-> every-link view (rescue hidden links)
TUNE_ARTICLE_LAYOUT = "E"  # TuneArticleLayout (Shift+E)
FORCE_REFRESH = "R"       # ForceRefresh (Shift+R)
INTERACTIVE_REFRESH = "I"  # InteractiveRefresh (Shift+I)


def choose_layout(t, delay: float = 0.15):
    """Drive the 'g l' AI-layout-wizard chord on TermTest ``t``."""
    t.send_keys(*CHOOSE_LAYOUT, delay=delay)


def summary_select(t, label_fragment: str, attempts: int = 8, delay: float = 0.35):
    """On the configured-site Layout summary card, walk the ▸ cursor to the
    option containing ``label_fragment`` and press Enter.

    Cursor-position-independent on purpose: the card's option list and default
    cursor have changed before (workspace-v2m8.4 added 'Fix links by hand' and
    moved the default off Close), and blind Up-times-N navigation silently
    selects the wrong row when that happens — navigate by what the screen SHOWS.
    """
    import time

    for _ in range(attempts):
        line = next((l for l in t.capture().splitlines() if "▸" in l), "")
        if label_fragment in line:
            t.send_keys("Enter")
            return
        t.send_keys("Up")
        time.sleep(delay)
    raise AssertionError(f"summary card cursor never reached {label_fragment!r}")


def summary_refine(t):
    """Select 'Refine the layout with AI' on the configured-site summary card."""
    summary_select(t, "Refine the layout")


def summary_fix_links(t):
    """Select 'Mark links to teach the AI' on the configured-site summary card (v2m8.4/nbvb.2)."""
    summary_select(t, "Mark links to teach")
