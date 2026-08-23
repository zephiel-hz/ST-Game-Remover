-- Create device_tokens table for token verification system
-- Run this in Supabase SQL Editor

CREATE TABLE IF NOT EXISTS device_tokens (
    token TEXT PRIMARY KEY,
    device_id TEXT,
    device_ids TEXT[] DEFAULT '{}'::TEXT[] CHECK (array_length(device_ids, 1) IS NULL OR array_length(device_ids, 1) <= 2),
    verified_at TIMESTAMP WITH TIME ZONE,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- Migration for existing databases: add array column and enforcement if needed
ALTER TABLE device_tokens ADD COLUMN IF NOT EXISTS device_ids TEXT[] DEFAULT '{}'::TEXT[];
UPDATE device_tokens
SET device_ids = CASE
    WHEN device_ids IS NULL OR array_length(device_ids, 1) IS NULL OR array_length(device_ids, 1) = 0 THEN ARRAY[device_id]
    ELSE device_ids
END
WHERE device_id IS NOT NULL;
ALTER TABLE device_tokens DROP CONSTRAINT IF EXISTS device_tokens_device_ids_check;
ALTER TABLE device_tokens ADD CONSTRAINT device_tokens_device_ids_check CHECK (array_length(device_ids, 1) IS NULL OR array_length(device_ids, 1) <= 2);

-- Create indices for faster lookups
CREATE INDEX IF NOT EXISTS idx_device_tokens_device_id ON device_tokens(device_id);
CREATE INDEX IF NOT EXISTS idx_device_tokens_device_ids ON device_tokens USING GIN (device_ids);
CREATE INDEX IF NOT EXISTS idx_device_tokens_verified_at ON device_tokens(verified_at);

-- Enable RLS (Row Level Security) for security
ALTER TABLE device_tokens ENABLE ROW LEVEL SECURITY;

-- Create policy allowing SELECT queries with anon key (for token verification)
DROP POLICY IF EXISTS "Allow public read access for token verification" ON device_tokens;
CREATE POLICY "Allow public read access for token verification" 
ON device_tokens 
FOR SELECT 
USING (true);

-- Allow token binding updates so a token can be used on up to two devices
DROP POLICY IF EXISTS "Allow update unverified tokens" ON device_tokens;
DROP POLICY IF EXISTS "Allow update token bindings" ON device_tokens;
CREATE POLICY "Allow update token bindings" 
ON device_tokens 
FOR UPDATE 
USING (true);

-- Grant permissions to anon role
GRANT SELECT ON device_tokens TO anon;
GRANT UPDATE ON device_tokens TO anon;
