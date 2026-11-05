const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');

const manifest = readFileSync(join(__dirname, '../07-mission-customers.yaml'), 'utf8');
const source = manifest.split('  customers.js: |\n')[1].split('\n---')[0]
  .split('\n').map(line => line.slice(4)).join('\n');

function run(env = {}, random = 0.5) {
  const calls = [];
  const context = {
    __ENV: { PORTAL_URL: 'https://portal.example/', CUSTOMER_KEY: 'test-key', ...env },
    http: {
      post: (...args) => { calls.push(['POST', ...args]); return { status: 504 }; },
      get: (...args) => { calls.push(['GET', ...args]); return { status: 200 }; },
    },
    check: (response, checks) => Object.values(checks).every(check => check(response)),
    Math: Object.assign(Object.create(Math), { random: () => random }),
  };
  vm.createContext(context);
  vm.runInContext(source.replace(/^import .*;$/gm, '').replace(/\bexport /g, '')
    + '\nglobalThis.script = { options, order, browse };', context);
  return { ...context.script, calls };
}

test('constant arrivals and lifecycle have no incident abort thresholds', () => {
  const { options } = run();
  assert.equal(options.scenarios.orders.rate, 90);
  assert.equal(options.scenarios.catalog.rate, 240);
  for (const scenario of Object.values(options.scenarios)) {
    assert.equal(scenario.executor, 'constant-arrival-rate');
    assert.equal(scenario.timeUnit, '1m');
    assert.equal(scenario.duration, '100m');
  }
  assert.equal(options.thresholds, undefined);
  assert.match(manifest, /activeDeadlineSeconds: 6600/);
  assert.match(manifest, /ttlSecondsAfterFinished: 600/);
  assert.match(manifest, /secretKeyRef: \{ name: mission-portal, key: customer-key \}/);
});

test('requests use matching catalog SKUs, key, channel mix and keep going after a 504', () => {
  for (const [random, channel] of [[0.59, 'web'], [0.6, 'mobile'], [0.89, 'mobile'], [0.9, 'partner']]) {
    const script = run({}, random);
    script.order();
    script.order();
    script.browse();
    assert.equal(script.calls.length, 3);
    const [, url, body, args] = script.calls[0];
    assert.equal(url, 'https://portal.example/api/portal/orders');
    assert.match(JSON.parse(body).sku, /^CT-00[1-6]$/);
    assert.ok(JSON.parse(body).quantity >= 1 && JSON.parse(body).quantity <= 3);
    assert.equal(args.headers['X-Lab-Customer-Key'], 'test-key');
    assert.equal(args.headers['X-Amlab-Channel'], channel);
    assert.equal(args.redirects, 0);
    assert.equal(script.calls[2][1], 'https://portal.example/api/portal/catalog');
  }
});

test('runtime configuration is validated before emitting load', () => {
  for (const env of [{ PORTAL_URL: '__PORTAL_URL__' }, { CUSTOMER_KEY: '' },
    { PORTAL_URL: 'https://portal.example?key=secret' }, { PORTAL_URL: 'https://user@portal.example' },
    { ORDERS_PER_MINUTE: '0' }, { BROWSES_PER_MINUTE: '601' }, { DURATION: '101m' }]) {
    assert.throws(() => run(env));
  }
  const script = run({ ORDERS_PER_MINUTE: '60', BROWSES_PER_MINUTE: '120', DURATION: '30s' });
  assert.equal(script.options.scenarios.orders.rate, 60);
  assert.equal(script.options.scenarios.catalog.duration, '30s');
});
