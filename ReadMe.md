# Latest Release v2.10.3.6 20th October, 2024

# 2.10.3.6 (2024-10-20)
- Updated code base to latest official code, including
	- added OE3IDE Connectors and Tools link to the about box links
	- moved release notes button from setup to the about box
	- variables that have name with _double now consider %precis=N%
	- resolved issue where redraw of console controls became very slow after 8th Oct Windows update

- Added HL2 database CLI option to shortcut
- Fix for non disablement of PA when disabled from xvtr form
- Added ability to select Alt Rx for transverter input in xvtr form
- Fix for HL2 of Tx attenuator in official codebase 
- Removed automatic renaming of database folder, the Thetis-HL2-x64 must be manually renamed to Thetis-x64
- Updated to allow RF off delay before releasing PTT for Tun & 2TON
- Updated to not over write txtPSpeak for PS
- Updated to handle existing database.bak
