-- Add storage metadata fields to hzmanifest_files for folder-based storage support
ALTER TABLE hzmanifest_files
ADD COLUMN IF NOT EXISTS storage_type TEXT DEFAULT 'zip';

ALTER TABLE hzmanifest_files
ADD COLUMN IF NOT EXISTS folder_path TEXT;

CREATE INDEX IF NOT EXISTS idx_hzmanifest_files_storage_type ON hzmanifest_files(storage_type);