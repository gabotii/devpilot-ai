import { describe, expect, it, vi } from 'vitest';
import { api, ApiError, onUnauthorized, qs, readCsrfToken } from './client';
import { safeNext } from '../lib/forms';
import { isOverdue } from '../lib/format';
import { mockApi } from '../test/utils';

describe('api client', () => {
  it('sends the CSRF token only on unsafe methods', async () => {
    document.cookie = 'devpilot_csrf=tok123; path=/';
    const { calls } = mockApi({ 'GET /api/x': { body: { ok: 1 } }, 'POST /api/x': { body: { ok: 2 } } });

    await api.get('/api/x');
    await api.post('/api/x', { a: 1 });

    expect(calls[0]?.headers['X-CSRF-Token']).toBeUndefined();
    expect(calls[1]?.headers['X-CSRF-Token']).toBe('tok123');
    expect(calls[1]?.headers['Content-Type']).toBe('application/json');
  });

  it('prefers the __Host- prefixed cookie when present', () => {
    // Browsers only accept __Host- cookies over HTTPS (jsdom too), so stub the cookie jar.
    vi.spyOn(document, 'cookie', 'get').mockReturnValue('other=1; __Host-devpilot_csrf=secure; devpilot_csrf=plain');
    expect(readCsrfToken()).toBe('secure');
  });

  it('maps problem details into ApiError with field errors', async () => {
    mockApi({
      'POST /api/x': {
        status: 400,
        body: { title: 'Validation failed', detail: 'One or more fields are invalid.', code: 'validation', errors: { name: ['Too short'] }, traceId: 't-1' },
      },
    });
    const error = await api.post('/api/x', {}).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.status).toBe(400);
    expect(apiError.code).toBe('validation');
    expect(apiError.fieldErrors.name).toEqual(['Too short']);
    expect(apiError.traceId).toBe('t-1');
  });

  it('notifies unauthorized listeners on 401 (except the login call itself)', async () => {
    mockApi({ 'GET /api/me': { status: 401, body: {} }, 'POST /api/auth/login': { status: 401, body: { detail: 'Invalid email or password.' } } });
    const listener = vi.fn();
    const unsubscribe = onUnauthorized(listener);

    await expect(api.get('/api/me')).rejects.toBeInstanceOf(ApiError);
    await expect(api.post('/api/auth/login', {})).rejects.toThrow('Invalid email or password.');
    unsubscribe();

    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('reports network failures as a friendly ApiError', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));
    const error = (await api.get('/api/x').catch((e: unknown) => e)) as ApiError;
    expect(error.code).toBe('network');
    expect(error.status).toBe(0);
  });

  it('returns undefined for 204 responses', async () => {
    mockApi({ 'DELETE /api/x': { status: 204 } });
    await expect(api.del('/api/x')).resolves.toBeUndefined();
  });

  it('builds query strings without empty values', () => {
    expect(qs({ q: 'a b', page: 2, empty: '', none: undefined, flag: false })).toBe('?q=a+b&page=2&flag=false');
    expect(qs({})).toBe('');
  });
});

describe('helpers', () => {
  it('safeNext only allows in-app relative paths', () => {
    expect(safeNext('/o/1/projects')).toBe('/o/1/projects');
    expect(safeNext('https://evil.example')).toBe('/');
    expect(safeNext('//evil.example')).toBe('/');
    expect(safeNext('/\\evil.example')).toBe('/');
    expect(safeNext(null)).toBe('/');
  });

  it('isOverdue ignores closed items and future dates', () => {
    const today = new Date(2026, 9, 11);
    expect(isOverdue('2026-10-10', 'Todo', today)).toBe(true);
    expect(isOverdue('2026-10-11', 'Todo', today)).toBe(false);
    expect(isOverdue('2026-10-01', 'Done', today)).toBe(false);
    expect(isOverdue(null, 'Todo', today)).toBe(false);
  });
});
