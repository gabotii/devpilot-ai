import { expect, test } from '@playwright/test';

declare const process: { env: Record<string, string | undefined> };

const SHOTS = process.env.E2E_SCREENSHOTS;

test('register, create org and project, manage a task end to end', async ({ page }) => {
  const email = `e2e-${Date.now()}@example.com`;

  await page.goto('/register');
  await page.getByLabel('Nombre').fill('Eva Ops');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Contraseña', { exact: true }).fill('correct-horse-battery-9');
  await page.getByLabel('Repite la contraseña').fill('correct-horse-battery-9');
  await page.getByRole('button', { name: 'Crear cuenta' }).click();

  await expect(page.getByRole('heading', { name: /Hola, Eva Ops/ })).toBeVisible();
  await page.getByLabel('Nombre de la organización').fill('Orbital Labs');
  await page.getByRole('button', { name: /Crear organización/ }).click();

  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();
  await page.getByRole('link', { name: 'Proyectos', exact: true }).click();
  await page.getByRole('button', { name: /Nuevo proyecto/ }).click();
  await page.getByRole('dialog').getByLabel('Nombre').fill('Checkout Service');
  await expect(page.getByRole('dialog').getByLabel('Clave')).toHaveValue('CS');
  await page.getByRole('dialog').getByRole('button', { name: 'Crear proyecto' }).click();

  await expect(page.getByRole('heading', { name: 'Checkout Service' })).toBeVisible();
  await expect(page.getByRole('region', { name: /Backlog/ })).toBeVisible();

  await page.getByRole('button', { name: /Nueva tarea/ }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Tipo').selectOption('Issue');
  await dialog.getByLabel('Prioridad').selectOption('Critical');
  await dialog.getByLabel('Título').fill('Payment confirmation times out');
  await dialog.getByRole('button', { name: 'Crear' }).click();

  await expect(page.getByRole('heading', { name: 'Payment confirmation times out' })).toBeVisible();
  await expect(page.getByText('CS-1')).toBeVisible();

  // Workflow: only allowed transitions are offered.
  await page.getByLabel('Estado').selectOption({ label: '→ En curso' });
  await expect(page.getByText('Estado actualizado')).toBeVisible();

  await page.getByLabel('Añadir comentario').fill('Reproduced on staging with a 30s gateway timeout.');
  await page.getByRole('button', { name: 'Comentar' }).click();
  await expect(page.getByText('Reproduced on staging with a 30s gateway timeout.')).toBeVisible();

  await page.getByRole('tab', { name: 'Historial' }).click();
  await expect(page.getByText(/cambió el estado/)).toBeVisible();

  await page.getByRole('link', { name: /Volver al tablero/ }).click();
  await expect(page.getByRole('region', { name: /En curso, 1 tareas/ })).toBeVisible();
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/board.png`, fullPage: true });

  await page.getByRole('link', { name: 'Dashboard' }).click();
  await expect(page.getByLabel('Incidencias abiertas: 1')).toBeVisible();
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/dashboard.png`, fullPage: true });

  // Logout invalidates the session.
  await page.getByRole('button', { name: 'Menú de usuario' }).click();
  await page.getByRole('menuitem', { name: /Cerrar sesión/ }).click();
  await expect(page.getByRole('heading', { name: 'Iniciar sesión' })).toBeVisible();
});
