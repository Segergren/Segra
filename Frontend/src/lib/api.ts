const API_BASE = 'https://segra.tv/api';

async function post(path: string, body: Record<string, unknown>) {
  const res = await fetch(`${API_BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  return res.json();
}

async function getWithToken(path: string, accessToken: string) {
  const res = await fetch(`${API_BASE}${path}`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  return res.json();
}

async function patchWithToken(path: string, accessToken: string, body: Record<string, unknown>) {
  const res = await fetch(`${API_BASE}${path}`, {
    method: 'PATCH',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify(body),
  });
  return res.json();
}

export const api = {
  login: (email: string, password: string) => post('/auth/login', { email, password }),

  register: (email: string, password: string) => post('/auth/register', { email, password }),

  refreshToken: (refresh_token: string) => post('/auth/token/refresh', { refresh_token }),

  getProfile: (accessToken: string) => getWithToken('/auth/profile', accessToken),

  updateProfile: (accessToken: string, data: Record<string, unknown>) =>
    patchWithToken('/auth/profile', accessToken, data),

  checkUsername: async (username: string) => {
    const res = await fetch(
      `${API_BASE}/users/check-username?username=${encodeURIComponent(username)}`,
    );
    return res.json();
  },

  uploadAvatar: async (accessToken: string, file: File) => {
    const formData = new FormData();
    formData.append('avatar', file);
    const res = await fetch(`${API_BASE}/user/avatar`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${accessToken}` },
      body: formData,
    });
    return res.json();
  },

  getDiscordLoginUrl: (desktopRedirect: string) =>
    `${API_BASE}/auth/login/discord?desktop_redirect=${encodeURIComponent(desktopRedirect)}`,
};
