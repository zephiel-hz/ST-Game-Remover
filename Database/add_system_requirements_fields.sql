-- Add System Requirements fields to hzmanifest_files table
-- Run this in Supabase SQL Editor to add minimum_requirements and recommended_requirements columns

-- Add minimum_requirements column if it doesn't exist
ALTER TABLE hzmanifest_files
ADD COLUMN IF NOT EXISTS minimum_requirements TEXT DEFAULT '';

-- Add recommended_requirements column if it doesn't exist
ALTER TABLE hzmanifest_files
ADD COLUMN IF NOT EXISTS recommended_requirements TEXT DEFAULT '';

-- Create index for better query performance on requirements (optional)
CREATE INDEX IF NOT EXISTS idx_hzmanifest_files_requirements 
ON hzmanifest_files(id) 
WHERE minimum_requirements IS NOT NULL OR recommended_requirements IS NOT NULL;

-- Verify the columns were added (you can run this to check)
-- SELECT id, name, minimum_requirements, recommended_requirements FROM hzmanifest_files LIMIT 5;
