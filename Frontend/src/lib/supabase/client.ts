import { createClient } from '@supabase/supabase-js';

const SUPABASE_URL = 'https://supabase.segra.tv';
const SUPABASE_ANON_KEY =
  'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlIjoiYW5vbiIsImlzcyI6InN1cGFiYXNlIiwiaWF0IjoxNzM3NjczMzI4LCJleHAiOjIwNTMyNDkzMjh9.MhhUzFqo2wSaMj0hN-59LrW0TJK388tpdFiXUSKhXnQ';

export const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY, {
  auth: {
    flowType: 'pkce',
    autoRefreshToken: true,
    persistSession: true,
    detectSessionInUrl: false,
  },
});
