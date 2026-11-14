// Tiny in-house faker for demo forms. Avoids pulling a real faker library
// into the SPA bundle. The pools are intentionally small — the random
// suffix on legal names keeps multiple clicks producing distinct rows.

const companies = [
  'Contoso', 'Acme', 'Globex', 'Initech', 'Umbrella', 'Soylent',
  'Stark Industries', 'Wayne Enterprises', 'Pied Piper', 'Hooli',
  'Massive Dynamic', 'Cyberdyne', 'Tyrell', 'Aperture', 'Black Mesa',
];

const suffixes = ['A/S', 'GmbH', 'Ltd', 'Inc', 'BV', 'AB', 'Oy', 'SA', 'NV'];

// Countries a regional hub claims (routing-keys showcase), so generated accounts split between
// the Nordic and DACH hubs. Type another country by hand to see the unrouted hub dead-letter it.
const countries = ['DE', 'AT', 'CH', 'DK', 'SE', 'NO', 'FI'];

const firstNames = [
  'Anna', 'Bjarke', 'Camilla', 'David', 'Elisabeth', 'Frederik',
  'Gitte', 'Henrik', 'Ida', 'Jens', 'Karen', 'Lars', 'Mette',
  'Niels', 'Ole', 'Pia', 'Rasmus', 'Signe', 'Thomas', 'Ulla',
];

const lastNames = [
  'Hansen', 'Nielsen', 'Jensen', 'Pedersen', 'Andersen', 'Sørensen',
  'Larsen', 'Christensen', 'Kristensen', 'Olsen', 'Rasmussen',
  'Madsen', 'Thomsen', 'Schmidt', 'Mortensen',
];

const emailDomains = ['example.com', 'example.dk', 'example.de', 'example.io', 'test.local'];

const phonePrefixes = ['+45', '+49', '+46', '+47', '+44', '+33', '+1'];

function pick<T>(arr: readonly T[]): T {
  return arr[Math.floor(Math.random() * arr.length)];
}

function shortSuffix(): string {
  return Math.random().toString(36).slice(2, 5).toUpperCase();
}

function randomDigits(n: number): string {
  let out = '';
  for (let i = 0; i < n; i++) out += Math.floor(Math.random() * 10).toString();
  return out;
}

export function randomCompany(): { legalName: string; countryCode: string; taxId: string } {
  return {
    legalName: `${pick(companies)} ${pick(suffixes)} ${shortSuffix()}`,
    countryCode: pick(countries),
    taxId: `TAX-${randomDigits(7)}`,
  };
}

export function randomPerson(): { firstName: string; lastName: string; email: string; phone: string } {
  const first = pick(firstNames);
  const last = pick(lastNames);
  const email = `${first.toLowerCase()}.${last.toLowerCase()}@${pick(emailDomains)}`;
  const phone = `${pick(phonePrefixes)} ${randomDigits(2)} ${randomDigits(2)} ${randomDigits(2)} ${randomDigits(2)}`;
  return { firstName: first, lastName: last, email, phone };
}

export function randomPick<T>(arr: readonly T[]): T | undefined {
  return arr.length ? arr[Math.floor(Math.random() * arr.length)] : undefined;
}
