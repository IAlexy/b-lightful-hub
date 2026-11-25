import { expect, test } from '@playwright/test';

test.describe('B-Lightful Hub — Development Environment Interface (UI) Tests', () => {
  test('Swagger UI loads and renders microservice endpoint groups', async ({ page }) => {
    await page.goto('/swagger/index.html');

    await expect(page.locator('#swagger-ui')).toBeVisible();
    await expect(page.locator('.swagger-ui .info .title')).toContainText('B2B.GraphQL.Gateway');
    await expect(page.getByText('/api/catalog/products').first()).toBeVisible();
    await expect(page.getByText('/api/customers').first()).toBeVisible();
    await expect(page.getByText('/graphql', { exact: true }).first()).toBeVisible();
  });

  test('Dev Portal (/dev-ui) loads seeded mockup data and KPIs', async ({ page }) => {
    await page.goto('/');
    await expect(page).toHaveURL(/\/dev-ui/);

    await expect(page.getByTestId('portal-title')).toHaveText('B-Lightful Hub — Dev Portal');
    await expect(page.getByTestId('env-badge')).toContainText('Development');
    await expect(page.getByTestId('seed-badge')).toContainText('Mockup Data: Ready');

    await expect(page.getByTestId('kpi-products')).not.toHaveText('-');
    const productsCount = Number(await page.getByTestId('kpi-products').textContent());
    expect(productsCount).toBeGreaterThanOrEqual(6);

    // Verify seeded mockup catalog rows are displayed
    await expect(page.getByTestId('catalog-row-SRV-RACK-42U')).toBeVisible();
    await expect(page.getByTestId('catalog-row-NVME-ARRAY-24TB')).toBeVisible();
    await expect(page.getByTestId('catalog-row-COOL-CDU-150KW')).toBeVisible();
  });

  test('Catalog tab filters by category and creates a product via UI form', async ({ page }) => {
    await page.goto('/dev-ui');
    await expect(page.getByTestId('catalog-row-NVME-ARRAY-24TB')).toBeVisible();

    // Filter by seeded category
    await page.getByTestId('catalog-category-filter').selectOption('Storage & NVMe');
    await expect(page.getByTestId('catalog-row-NVME-ARRAY-24TB')).toBeVisible();
    await expect(page.getByTestId('catalog-row-SRV-RACK-42U')).toHaveCount(0);

    // Reset filter and create a new product via the form
    await page.getByTestId('catalog-category-filter').selectOption('');
    const sku = `UI-SKU-${Date.now()}`;
    await page.getByTestId('product-sku-input').fill(sku);
    await page.getByTestId('product-name-input').fill('Playwright UI Optical Transceiver');
    await page.getByTestId('product-category-input').fill('Networking');
    await page.getByTestId('create-product-submit').click();

    await expect(page.getByTestId('product-status')).toContainText(`Product ${sku} created.`);
    await expect(page.getByTestId(`catalog-row-${sku}`)).toBeVisible();
  });

  test('Customers tab shows seeded B2B customers and creates a new customer', async ({ page }) => {
    await page.goto('/dev-ui');
    await page.getByTestId('tab-customers').click();
    await expect(page.getByTestId('panel-customers')).toBeVisible();

    await expect(page.getByTestId('customer-row-42889123456001')).toBeVisible();
    await expect(page.getByTestId('customer-row-51234098700024')).toBeVisible();
    await expect(page.getByTestId('customer-row-63490123400039')).toBeVisible();

    const siren = `771${Date.now().toString().slice(-11)}`;
    await page.getByTestId('customer-name-input').fill('Playwright UI Photonics SAS');
    await page.getByTestId('customer-siren-input').fill(siren);
    await page.getByTestId('customer-address-input').fill('15 Rue du Test UI, 31000 Toulouse');
    await page.getByTestId('customer-email-input').fill('ui-test@photonics.fr');
    await page.getByTestId('create-customer-submit').click();

    await expect(page.getByTestId('customer-status')).toContainText('Customer Playwright UI Photonics SAS created.');
    await expect(page.getByTestId(`customer-row-${siren}`)).toBeVisible();
  });

  test('Orders & GraphQL tabs allow placing an order and running a federated query', async ({ page }) => {
    await page.goto('/dev-ui');
    await page.getByTestId('tab-orders').click();
    await expect(page.getByTestId('panel-orders')).toBeVisible();
    await expect(page.locator('[data-testid^="order-row-"]').first()).toBeVisible();

    const initialOrderRows = await page.locator('[data-testid^="order-row-"]').count();
    expect(initialOrderRows).toBeGreaterThanOrEqual(3);

    await page.getByTestId('order-sku-input').fill('FW-EDGE-40G');
    await page.getByTestId('order-qty-input').fill('2');
    await page.getByTestId('create-order-submit').click();

    await expect(page.getByTestId('order-status')).toContainText('confirmed.');
    await expect(page.locator('[data-testid^="order-row-"]')).toHaveCount(initialOrderRows + 1);

    // Switch to GraphQL Explorer and execute federated query
    await page.getByTestId('tab-graphql').click();
    await page.getByTestId('graphql-run-btn').click();
    await expect(page.getByTestId('graphql-result')).toContainText('"dashboard"');
    await expect(page.getByTestId('graphql-result')).toContainText('"orders"');
    await expect(page.getByTestId('graphql-result')).toContainText('"errors": null');
  });
});
