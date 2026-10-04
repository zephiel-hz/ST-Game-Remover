# Changelog

All notable changes to **HZ Lua Manager** (formerly Steam Plugin Manager) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [3.0.2] - 2026-10-04

### Added
* **In-App Changelog Viewer:** You can now view the full release notes directly from Settings, complete with a quick search filter and a shortcut to GitHub Releases.

### Changed
* **Instant Offline Status:** Closing the app or signing out now marks you offline immediately without any delay.

### Fixed
* **Realtime Profile Pictures:** Other users' new profile pictures now update right away in Community Chat without needing to restart the app.
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

## [3.0.0] - 2026-09-29

### UI Overhaul
* **Game Library Redesign:** Cleaner, modern, and responsive card-based layout.
* **Enhanced Game Cards:** Features installed status badges, optimized high-resolution cover art, and smooth hover animations.
* **Faster Filtering:** Improved game search and category filtering for rapid catalog browsing.
* **New Main Header:** Integrated user profile info, token status, and Steam engine status indicator.
* **Modern Dynamic Sidebar:** Smooth tab transitions with active indicator rails and non-intrusive floating chat button positioning.
* **Multi-Theme Support:** Added customizable themes including Dark, Dark Blue, Dark Purple, Dark Green, Dark Red, Dark Pink, and Dark Yellow.
* **Modern Dialog System:** Replaced standard Windows dialogs with custom themed components (`ModernMessageBox`, `ModernDialogWindow`, `DeleteChoiceDialog`, `StyledMessageDialog`).
* **Markdown Update Notes:** Update dialog renders GitHub Markdown formatting with styled headers, lists, and links.

### New Game Request System
* **Interactive Request Modal:** Modern interface (`RequestGameModalView`) for structured game and manifest requests.
* **Hubcap & ManifestHub Integration:** Automated manifest search and retrieval through integrated API services.
* **Hubcap Authentication:** In-app login and token management dialog for authenticated requests.
* **Realtime Operation Progress:** Multi-step progress modal (`OperationProgressModalView`) displaying live download, extraction, compression, and deployment status.
* **OnlineFix Pipeline:** Automated companion resource workflow to synchronize available fixes upon manifest request.

### Community Chat & Profiles
* **Realtime Chat Engine:** Direct messaging powered by Supabase Realtime / WebSocket connectivity.
* **Adaptive Message Bubbles:** Content-hugging chat bubbles with clear alignment (own messages right, others left).
* **Message Replies:** Reply directly to messages with quote previews and one-click jump navigation with highlighting.
* **User Mentions (@username):** Mention autocomplete popup, visual mention badge styling, and clickable profile links.
* **Interactive Profile Cards:** Quick-access popup profile cards accessible from avatars, usernames, and mentions.
* **Text Selection & Auto-Links:** Select and copy text directly from message bubbles with automatic clickable URL detection.
* **Profile Customization:** Customize display names and choose preset avatar colors.

### Performance & Architecture
* **Dynamic Versioning:** Centralized version management via `AppInfo` dynamically reading assembly metadata, eliminating hardcoded version strings.
* **Secure Configuration:** Separated sensitive API keys and service configurations from source code and build pipelines.
* **View Caching:** Instant tab switching with preserved scroll positions and state across views.
* **Optimized Responsiveness:** Reduced UI thread blocking during disk and network operations.

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
