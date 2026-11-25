import { expect, test } from '@playwright/test';

test.describe('B-Lightful Hub — Development Environment API Tests', () => {
  test('GET /health reports healthy Development environment with seeded mockup data', async ({ request }) => {
    const res = await request.get('/health');
    expect(res.status()).toBe(200);

    const body = await res.json();
    expect(body.status).toBe('healthy');
    expect(body.environment).toBe('Development');
    expect(body.mockDataSeeded).toBe(true);
    expect(body.summary.products).toBeGreaterThanOrEqual(6);
    expect(body.summary.customers).toBeGreaterThanOrEqual(3);
    expect(body.summary.orders).toBeGreaterThanOrEqual(3);
    expect(body.summary.invoices).toBeGreaterThanOrEqual(2);
    expect(body.summary.shipments).toBeGreaterThanOrEqual(2);
  });

  test('GET /swagger/v1/swagger.json exposes OpenAPI specification in Development', async ({ request }) => {
    const res = await request.get('/swagger/v1/swagger.json');
    expect(res.status()).toBe(200);

    const spec = await res.json();
    expect(spec.openapi).toBeTruthy();
    expect(spec.paths['/api/catalog/products']).toBeTruthy();
    expect(spec.paths['/api/customers']).toBeTruthy();
    expect(spec.paths['/api/ordering/orders']).toBeTruthy();
    expect(spec.paths['/graphql']).toBeTruthy();
  });

  test('Catalog API serves seeded mockup products and creates a new SKU', async ({ request }) => {
    const listRes = await request.get('/api/catalog/products');
    expect(listRes.status()).toBe(200);
    const products = await listRes.json();
    const skus = products.map((p: { sku: string }) => p.sku);

    expect(skus).toContain('SRV-RACK-42U');
    expect(skus).toContain('NVME-ARRAY-24TB');
    expect(skus).toContain('COOL-CDU-150KW');

    const uniqueSku = `E2E-API-${Date.now()}`;
    const createRes = await request.post('/api/catalog/products', {
      data: {
        sku: uniqueSku,
        name: 'Playwright API Test Switch 400G',
        category: 'Networking',
        unitPriceHt: 5200,
        bulkTierMinQuantity: 4,
        bulkUnitPriceHt: 4600,
        initialStock: 18,
        unitWeightKg: 8.4
      }
    });
    expect(createRes.status()).toBe(201);
    const created = await createRes.json();
    expect(created.sku).toBe(uniqueSku);

    const filteredRes = await request.get('/api/catalog/products?category=Networking');
    expect(filteredRes.status()).toBe(200);
    const filtered = await filteredRes.json();
    expect(filtered.some((p: { sku: string }) => p.sku === uniqueSku)).toBe(true);
  });

  test('Customer API serves seeded mockup accounts, enforces contact rule, and supports CRUD', async ({ request }) => {
    const listRes = await request.get('/api/customers');
    expect(listRes.status()).toBe(200);
    const customers = await listRes.json();
    expect(customers.some((c: { name: string }) => c.name === 'Alpine Cloud Datacenters SAS')).toBe(true);
    expect(customers.some((c: { name: string }) => c.name === 'Helios Industrial Logistics SA')).toBe(true);
    expect(customers.some((c: { name: string }) => c.name === 'Nordic Photonics Systems AB')).toBe(true);

    // Validation rule: must have email or phone
    const invalidRes = await request.post('/api/customers', {
      data: {
        name: 'No Contact Corp',
        sirenSiret: `999${Date.now().toString().slice(-11)}`,
        invoicingAddress: '1 Rue Test, 75001 Paris',
        useInvoicingAddressForDelivery: true,
        contactEmail: null,
        phoneNumber: null
      }
    });
    expect(invalidRes.status()).toBe(400);
    const errBody = await invalidRes.json();
    expect(errBody.errorCode).toBe('MISSING_CONTACT_METHOD');

    // Create -> Get -> Delete
    const uniqueSiren = `888${Date.now().toString().slice(-11)}`;
    const createRes = await request.post('/api/customers', {
      data: {
        name: 'Playwright API Logistics SAS',
        sirenSiret: uniqueSiren,
        invoicingAddress: '42 Avenue de la Republique, 69002 Lyon',
        deliveryAddress: null,
        useInvoicingAddressForDelivery: true,
        contactEmail: 'api-e2e@playwright-logistics.fr',
        phoneNumber: '+33 4 72 00 99 88'
      }
    });
    expect(createRes.status()).toBe(201);
    const created = await createRes.json();
    expect(created.deliveryAddress).toBe(created.invoicingAddress);

    const getRes = await request.get(`/api/customers/${created.id}`);
    expect(getRes.status()).toBe(200);

    const delRes = await request.delete(`/api/customers/${created.id}`);
    expect(delRes.status()).toBe(200);
  });

  test('Ordering Saga API creates order -> invoice -> shipment and supports cancellation', async ({ request }) => {
    const createOrderRes = await request.post('/api/ordering/orders', {
      data: {
        buyerCompanyName: 'Playwright Order Test Corp',
        buyerVatNumber: 'FR9011223344',
        paymentTermsDays: 30,
        shippingDestination: 'Nantes Tech Park, 44000 France',
        items: [{ sku: 'FW-EDGE-40G', quantity: 2 }],
        autoConfirm: true
      }
    });
    expect(createOrderRes.status()).toBe(201);
    const order = await createOrderRes.json();
    expect(order.status).toBe('Confirmed');

    const invoices = await (await request.get('/api/invoicing/invoices')).json();
    expect(invoices.some((i: { orderId: string }) => i.orderId === order.id)).toBe(true);

    const shipments = await (await request.get('/api/delivery/shipments')).json();
    expect(shipments.some((s: { orderId: string }) => s.orderId === order.id)).toBe(true);

    const cancelRes = await request.delete(
      `/api/ordering/orders/${order.id}?reason=Playwright%20API%20cleanup`
    );
    expect(cancelRes.status()).toBe(200);
    const cancelBody = await cancelRes.json();
    expect(cancelBody.status).toBe('Cancelled');
  });

  test('Federated GraphQL API returns SDL schema and stitched dashboard + orders', async ({ request }) => {
    const sdlRes = await request.get('/graphql/schema');
    expect(sdlRes.status()).toBe(200);
    expect(await sdlRes.text()).toContain('type FederatedOrderNode');

    const gqlRes = await request.post('/graphql', {
      data: {
        query:
          'query { dashboard { totalProducts totalOrders totalInvoices activeShipments } orders { id orderNumber buyerCompanyName invoice { invoiceNumber totalTtc } shipment { trackingNumber status } } }'
      }
    });
    expect(gqlRes.status()).toBe(200);
    const gql = await gqlRes.json();
    expect(gql.errors).toBeNull();
    expect(gql.data.dashboard.totalProducts).toBeGreaterThanOrEqual(6);
    expect(gql.data.orders.length).toBeGreaterThanOrEqual(3);
  });
});
