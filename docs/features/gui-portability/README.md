# Portable inventories and saved browsing sessions

Use the portable inventory export action in the File menu to save the active
database panel as a standalone SQLite file. Choose a new destination file. The
export preserves the inventory for offline browsing and refuses to replace an
existing file.

The app saves both panels' current folders when it closes. For inventory panels,
it also remembers the database file and selected root. It restores the active
panel, panel split, and resizable column widths at the next launch. If a database
is unavailable, that panel keeps its current folder and the other panel can still
restore. If a saved root or folder is missing, the app selects an available root
or the nearest indexed ancestor and reports the fallback.

An inventory's recorded root path remains visible when its original drive is
offline. The panel also reports whether that path is available on the current
computer; offline browsing does not depend on the recorded drive being mounted.
