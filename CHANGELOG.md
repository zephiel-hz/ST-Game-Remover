# Changelog

---

## [3.1.1] - 2026-10-23

### Added
* **Online-Fix Restore Feature:** Easily restore modified games to their original state by automatically removing tracked Online-Fix files and triggering Steam file integrity verification.

### Fixed
* **Clean Game Uninstall:** Fixed an issue where uninstalling games left leftover files and folders behind due to a manifest detection deadlock; game folders and unmanaged files are now completely wiped upon uninstall completion.
* **Online-Fix File Validation Protection:** Temporarily disables applying Online-Fix while Steam is actively validating or verifying game files to avoid conflicts.
* **Game Library Selection Gesture:** Fixed selection mode activation so hold/long-press on library cards triggers consistently on the first attempt.

---

## [3.1.0] - 2026-10-20

### Added
* **Custom Launch Configuration:** Configure custom executable targets (`.exe`), batch scripts (`.bat`, `.cmd`), custom arguments, and working directories directly from the Game Library detail view.
* **Authentic Desktop Shortcuts:** Create desktop shortcuts that follow your custom launch configurations while automatically resolving and preserving authentic original game icons.
* **Custom Launch Tutorials & One-Click Auto-Apply:** Game Bypass and Online-Fix detail views now feature step-by-step custom launch guides with an **Auto-Apply Settings** button to configure launch settings with one click.
* **GameBypass Publisher & DRM Categorization:** Organized GameBypass catalog into publisher and DRM categories (Denuvo, Ubisoft, EA, Rockstar, and Others) with interactive filter badges.
* **Seamless In-App Library Navigation:** Added quick "Open Game in Library" navigation from Game Bypass and Online-Fix pages to immediately jump to your local library game details.

### Changed
* **Smooth & Non-Blocking Game Library:** Library refresh, game status detection, and library data updates are now completely asynchronous on background threads, eliminating UI freeze and lag.
* **Instant Batch Manifest Scanning:** Rewrote Steam library scanning to use single-pass batch manifest mapping, drastically speeding up game loading from seconds to milliseconds.
* **In-Place Game List Synchronization:** Refreshing the game library now updates cards in-place without rebuilding the visual tree, eliminating screen flicker and preserving scroll positions.
* **Custom Launch Safeguards:** Added checks ensuring custom launch settings and auto-apply tutorials can only be configured if the game is already installed in your local Steam library.

### Fixed
* **Installation & Download Action States:** Action buttons now correctly adapt to real-time installed, not installed, and downloading states.
* **Sub-DLC Detection Accuracy:** Improved accuracy of installed detection for sub-DLC items across Steam libraries.
* **Cloud Storage Streaming:** Enhanced connection stability and download streaming for GameBypass and Online-Fix asset packages.

---

## [3.0.5] - 2026-10-10

### Added
* **Auto Defender Exclusion:** The app now automatically requests Administrator privileges on startup to add required folders (such as the app Temp folder, Steam installation folder, and all detected SteamLibrary folders) to Windows Defender exclusions. This helps prevent false-positive antivirus detections and ensures game patches apply smoothly.

---

## [3.0.4] - 2026-10-08 [HOTFIX]

### Fixed
* **Add to Library Fix:** Fixed an issue where adding certain games (such as ELDEN RING and others) from HZ Manifest resulted in a "File not available in storage" error.
* **Storage Availability & Download Fallback:** Enhanced cloud storage item detection and introduced smart multi-tier fallback checks to ensure manifest and Lua files download reliably across all game titles.

---

## [3.0.3] - 2026-10-08

### Fixed
* **Hubcap API Key Auto-Regeneration:** Fixed an issue where expired or invalid Hubcap API keys caused Game Requests or Denuvo checks to fail; the app now automatically verifies and regenerates active keys directly from hubcapmanifest.
* **Game Request Reliability:** Added seamless background session handling and automatic retries when searching and downloading game manifests and Lua scripts.

---

## [3.0.2] - 2026-10-04

### Added
* **In-App Changelog Viewer:** You can now view the full release notes directly from Settings, complete with a quick search filter and a shortcut to GitHub Releases.

### Changed
* **Instant Offline Status:** Closing the app or signing out now marks you offline immediately without any delay.

### Fixed
* **Realtime Profile Pictures:** Other users' new profile pictures now update right away in Community Chat without needing to restart the app.
* **OnlineFix Upload Timeout:** Fixed an issue where uploading OnlineFix files to Backblaze B2 could encounter timeout errors.
* **B2 Storage Verification:** Ensured profile picture uploads are fully verified on Backblaze B2 before saving changes, preventing broken avatar displays.
* **Game Thumbnail Loading:** Fixed an issue where missing game thumbnails could cause background errors.

---

## [3.0.1] - 2026-10-03

### Added
* **Custom Profile Pictures:** You can now upload and crop your own avatar picture with an interactive circular preview.
* **Backblaze B2 Cloud Storage:** Switched storage backend to Backblaze B2 for faster and more reliable avatar and game resource downloads.
* **New `.hzbak` Backup Format:** Game backups now use the `.hzbak` format, with easy import and restore support in Game Library.
* **Pinned Chat Announcements:** Announcements in Community Chat now appear in a neat scrolling marquee banner above the messages.

### Changed
* **Modern Toggle Switches:** Redesigned toggle switches in Settings with smoother animations and modern styling.

### Fixed
* **OnlineFix Subfolder Extraction:** Fixed an issue where game patches containing engine subfolders (such as `Engine/`, `Binaries/`, or `Game_Data/`) were extracted into incorrect directories.
* **OnlineFix Archive Compression:** Improved patch archive structure by automatically stripping redundant wrapper folders so fix files deploy directly into the game root.
* **Presence & Stability:** Improved user status accuracy and chat connection stability.

---

## [3.0.0] - 2026-09-29 [MAJOR UPDATE]

### Added
* **Community Chat:** Chat in real time with other users, reply to messages, mention friends with `@username`, and check user profiles.
* **Game Request System:** Request games directly inside the app with live status tracking and automatic patch setup.
* **New Color Themes:** Added multiple themes including Dark Blue, Purple, Green, Red, Pink, and Yellow in Settings.
* **Rich Markdown Updates:** Update announcements now display styled text, bullet points, and clickable links.

### Changed
* **Modern UI Redesign:** Completely refreshed interface with modern game cards, smooth tab navigation, and updated headers.
* **Faster Game Browsing:** Improved game search and category filtering for quicker browsing in Game Library.
* **Modern Dialogs:** Replaced standard Windows message boxes with custom dark-themed dialogs.

### Fixed
* **App Performance & Smoothness:** Optimized memory usage and responsiveness for faster and smoother navigation throughout the app.

---

## [2.2.3] - 2026-09-20

### Fixed
* **Discord Webhook Restoration:** Updated notification infrastructure to restore features impacted by Discord webhook security deprecations.
* **Token Verification:** Improved token validation resilience and Steam plugin management reliability.

---

## [2.2.2] - 2026-09-13

### Added
* **In-App Bug Reporting:** Added a dedicated Bug Report section in Settings for sending issue reports directly to developers.

### Fixed
* **Network Connectivity:** Resolved an issue causing intermittent connection drops; added manual SteamTools reinstallation option in Settings.

---

## [2.2.1] - 2026-08-23

### Added
* **In-App WebView Token Generator:** Built-in token generator accessible directly within the application.
* **Additional Themes:** Expanded theme options in Settings for greater visual personalization.

### Changed
* **UI/UX Polishing:** Minor visual adjustments for enhanced interface consistency.

---

## [2.2.0] - 2026-08-08

### Added
* **Unlocker Toggle Switch:** Dedicated switch to enable or disable the Unlocker without uninstalling SteamTools.
* **Saweria Donation WebView:** Integrated donation interface via Saweria accessible directly in-app.
* **New Manifest Categories:** Added Horror, Souls-like, and Roguelike genre filters.

### Fixed
* **Stability:** Fixed minor bugs and improved overall application stability.

---

## [2.1.9] - 2026-08-04

### Changed
* **Comprehensive UI Overhaul:** Complete redesign across Dashboard, Sidebar, Splash Screen, and Welcome Screen.
* **Premium Dark Orange Theme:** Introduced new default dark orange aesthetic.
* **Redesigned Dialogs:** Updated Verify Token, Request Game, and Customize Profile modals.
* **Menu Enhancements:** Refreshed HZ Manifest, Game Bypass, and OnlineFix views.

---

## [2.1.8] - 2026-07-26

### Added
* **Saweria Support Button:** Added donation button enabling users to support the developer.

### Changed
* **Image Caching:** Optimized cover and banner image caching for Game Bypass and OnlineFix menus.

---

## [2.1.7] - 2026-07-20

### Fixed
* **Content Filter Hotfix:** Fixed adult (18+) content inadvertently appearing on the Dashboard when the adult filter was disabled.

---

## [2.1.6] - 2026-07-13

### Fixed
* **Catalog Alignment:** Fixed missing game entries caused by database synchronization mismatches.

---

## [2.1.5] - 2026-07-09

### Added
* **Startup Splash Animation:** Added animated loading sequence during application startup.
* **Game Library Import Button:** Dedicated Import button supporting Lua scripts, manifests, and archives.
* **DLC Detection:** Manifest details now list included DLCs parsed directly from Lua configuration.

### Fixed
* **Download Handling:** Fixed intermittent download failures in the HZ Manifest catalog.

---

## [2.1.4] - 2026-06-29

### Added
* **SteamTools Updater:** Added direct update action button for SteamTools in Settings.
* **Download Badges:** Added download counter badges on manifest catalog cards.

### Fixed
* **Modal Visibility:** Fixed visibility toggle issue on Customize Profile modal.
* **UI Alignment:** Fixed text alignment in genre badges and optimized banner image caching.

---

## [2.1.3] - 2026-06-22

### Added
* **Hot Reload:** Added Hot Reload support in Game Library to reflect file system changes immediately.
* **Category Filters:** Added genre and category filter dropdown in Game Library.
* **SteamDB URL Parsing:** Supported direct SteamDB URLs in the Game Request dialog.

### Changed
* **Layout Improvements:** Refined Dashboard and Sidebar layout spacing.

### Fixed
* **Adult Filter:** Fixed adult content filter edge cases on the Dashboard.

---

## [2.1.2] - 2026-05-28

### Changed
* **Unlocker Service Migration:** Transitioned the unlocker service framework from Steamtools to OpenSteamtool.
* **Expanded Device Allowance:** Increased single-token activation limit to up to 2 devices.

---

## [2.1.1] - 2026-05-24

### Added
* **Latest Uploads on Dashboard:** Dashboard now highlights the newest uploaded manifest files.
* **Navigation Sidebar:** Introduced dedicated sidebar rail for rapid menu switching.
* **HZ Manifest Pagination:** Added pagination to the manifest catalog for improved responsiveness.

### Security
* **Device-Based Activation:** Implemented hardware-bound device activation checks.
* **Startup Token Validation:** Strict token validation enforced during application boot.

---

## [1.11.4] - 2026-05-17

### Added
* **Game Request Feature:** Initial implementation of the user game request mechanism.

### Fixed
* Minor bug fixes and performance optimizations.

---

## [1.1.0] - [1.11.3] - 2026-01-29 to 2026-05-15

### Changed
* Incremental maintenance patches, performance enhancements, database catalog schema alignments, and stability improvements across releases 1.1.0 through 1.11.3.

---

## [1.0.8] - 2026-01-23

### Added
* Added option in Settings to toggle and disable hardware acceleration.

---

## [1.0.7] - 2026-01-21

### Added
* Added drag-and-drop file import support for Lua and manifest files directly into the Game Library.

---

## [1.0.6] - 2026-01-16

### Added
* Initial public release of Steam Plugin Manager (HZ Lua Manager).
