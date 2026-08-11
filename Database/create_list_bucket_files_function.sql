-- SQL Script: Create RPC function untuk list bucket files
-- Jalankan di Supabase SQL Editor (Database > SQL)

-- Drop existing function first
DROP FUNCTION IF EXISTS list_bucket_files(TEXT) CASCADE;

-- Function untuk list files dari bucket
CREATE OR REPLACE FUNCTION list_bucket_files(bucket_name TEXT)
RETURNS TABLE (name TEXT, file_id TEXT, updated_at TIMESTAMP WITH TIME ZONE, metadata JSONB)
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
BEGIN
  RETURN QUERY
  SELECT 
    o.name::TEXT,
    o.id::TEXT as file_id,
    o.updated_at,
    o.metadata
  FROM storage.objects o
  WHERE o.bucket_id = (
    SELECT b.id FROM storage.buckets b WHERE b.name = bucket_name
  )
  ORDER BY o.created_at DESC
  LIMIT 1000;
END;
$$ ;

-- Grant access
GRANT EXECUTE ON FUNCTION list_bucket_files(TEXT) TO authenticated, anon, service_role;

-- Verify
SELECT COUNT(*) as total_files 
FROM storage.objects 
WHERE bucket_id = (SELECT id FROM storage.buckets WHERE name = 'hzmanifest');
