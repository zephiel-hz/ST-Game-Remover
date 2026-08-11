-- Create user_profiles table for profile customization
-- Run this in Supabase SQL Editor

CREATE TABLE IF NOT EXISTS user_profiles (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    device_id TEXT NOT NULL UNIQUE,
    display_name TEXT,
    bio TEXT,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- Create index for faster lookups by device_id
CREATE INDEX IF NOT EXISTS idx_user_profiles_device_id ON user_profiles(device_id);

-- Enable RLS (Row Level Security)
ALTER TABLE user_profiles ENABLE ROW LEVEL SECURITY;

-- Drop existing policies if they exist to avoid conflicts
DROP POLICY IF EXISTS "Allow public select on user_profiles" ON user_profiles;
DROP POLICY IF EXISTS "Allow public insert on user_profiles" ON user_profiles;
DROP POLICY IF EXISTS "Allow public update on user_profiles" ON user_profiles;

-- Allow public read access
CREATE POLICY "Allow public select on user_profiles"
ON user_profiles
FOR SELECT
TO public, anon, authenticated
USING (true);

-- Allow public insert (for new profiles)
CREATE POLICY "Allow public insert on user_profiles"
ON user_profiles
FOR INSERT
TO public, anon, authenticated
WITH CHECK (true);

-- Allow public update (for profile modifications)
CREATE POLICY "Allow public update on user_profiles"
ON user_profiles
FOR UPDATE
TO public, anon, authenticated
USING (true)
WITH CHECK (true);

-- Grant necessary permissions
GRANT SELECT, INSERT, UPDATE ON user_profiles TO anon;
GRANT SELECT, INSERT, UPDATE ON user_profiles TO authenticated;
GRANT SELECT, INSERT, UPDATE ON user_profiles TO public;

-- Grant permissions on the sequence (for IDENTITY columns)
GRANT USAGE, SELECT ON SEQUENCE user_profiles_id_seq TO anon;
GRANT USAGE, SELECT ON SEQUENCE user_profiles_id_seq TO authenticated;
GRANT USAGE, SELECT ON SEQUENCE user_profiles_id_seq TO public;

-- Insert sample profile data (optional - comment out if not needed)
-- INSERT INTO user_profiles (device_id, display_name, bio)
-- VALUES ('sample-device-001', 'zephiel', 'Profile created for Zephiel.')
-- ON CONFLICT (device_id) DO NOTHING;
