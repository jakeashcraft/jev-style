// Builds reference.json for the parity tests from bench.mts itself. The part of bench.mts that reads the CSV,
// compiles each row, collects literals, loads or generates data/tags.json and computes the expected answers is
// copied unchanged (everything before `const RULES =`, minus the SDK import) and run on two scenarios. A dump
// step appended to that copy records rows, skipped lines, literals, domains, the snapshot, a fresh
// generateSnapshot() and the answer of every row for a set of seeded random states.
//
//   node tests/JevStyle.Benchmark.Tests/Fixtures/generate-reference.mjs ..\..\typesafe-ai
//
// Needs Node 22.6 or later (type stripping runs bench.mts's TypeScript directly).
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = process.argv[2];
assert(repo, 'usage: node generate-reference.mjs <path to jev-expression-benchmark>');
const source = readFileSync(join(repo, 'bench.mts'), 'utf8');
const cut = source.indexOf('\nconst RULES =');
assert(cut > 0, 'bench.mts has no `const RULES =` line; update this script');
const sdkImport = /^import \{[^}]*\} from '@typesafe-ai\/sdk';\r?\n/m;
assert(sdkImport.test(source), 'bench.mts no longer imports the SDK the way this script expects');
let core = source.slice(0, cut).replace(sdkImport, '');
const rulesStatement = /\nconst RULES =([\s\S]*?);\r?\n/.exec(source);
assert(rulesStatement, 'bench.mts has no RULES constant');
const rules = new Function(`return (${rulesStatement[1]});`)();
for (const marker of ['const TOKENS = ', 'const compile = ', 'function domainOf(', 'function generateSnapshot()', "new URL('data/tags.json', import.meta.url)"]) {
  assert(core.includes(marker), `bench.mts changed: ${marker} not found`);
}

core += `
{
  // Appended by generate-reference.mjs: everything above is bench.mts unchanged.
  let seed = 12345;
  const next = () => { seed = (Math.imul(seed, 1103515245) + 12345) >>> 0; return seed / 4294967296; };
  const domains = [...literals].map(([tag, compared]) => [tag, domainOf(compared)]);
  const states = [];
  for (let i = 0; i < Number(process.env.REFERENCE_STATES); i++) {
    const state = Object.fromEntries(domains.map(([tag, domain]) => [tag, domain[Math.floor(next() * domain.length)]]));
    states.push({ state, truth: rows.map(row => (evaluateLocally(row, state) ? '1' : '0')).join('') });
  }
  writeFileSync(process.env.REFERENCE_OUT, JSON.stringify({
    rows: rows.map(row => ({ id: row.id, name: row.name, expr: row.expr, tags: row.tags, expected: row.expected })),
    skipped: skipped.map(text => Number(/^line (\\d+):/.exec(text)[1])),
    literals: [...literals].map(([tag, compared]) => [tag, [...compared]]),
    domains,
    snapshot,
    snapshotText: readFileSync(snapshotFile, 'utf8'),
    generated: generateSnapshot(),
    // bench.mts's buildRequests state: Object.fromEntries(chunk.flatMap(row => row.tags).map(tag => [tag, snapshot[tag]]))
    batchStates: [1, 50].map(batch => [0, 1, 2].map(i => rows.slice(i * batch, (i + 1) * batch))
      .filter(chunk => chunk.length)
      .map(chunk => JSON.stringify(Object.fromEntries(chunk.flatMap(row => row.tags).map(tag => [tag, snapshot[tag]]))))),
    states,
  }));
}
`;

// Rows bench.mts accepts and rows it skips, in the documented grammar. Numbers with leading zeros, exponents or
// hex digits are left out on purpose: JavaScript reads them as octal or exponent literals, the C# port rejects them.
const tab = '\t';
const nbsp = String.fromCharCode(0xa0);
const lineSeparator = String.fromCharCode(0x2028);
const bom = String.fromCharCode(0xfeff);
const edgeLines = [
  'Name,ExpressionScript',
  'Plain,A == true && T > 150',
  'Or,A == false || T < 110',
  'Precedence,A == true || B == true && C == true',
  'Not before equality,!A == B',
  'Relational before equality,A == T > 150',
  'Chained relational,T < U < 2',
  'Double not,!!A && !(B || C)',
  'Loose equality,A == 1',
  'Loose inequality,B != 0 && T >= 151',
  'Boolean in relational,A > 0',
  'Literal case,A == TRUE || B == False',
  'Negative literal,N > -1 && N <= 3',
  'Decimals,F >= 1.5 && F < 2.25',
  'Subtraction,T -1 > 149',
  'Subtraction of literals,N > 1-1',
  'Operand value,A && T',
  'Or value,(T || A) == 151',
  'Bare tag,C',
  'Constant,1 == 1',
  'Literal only,TRUE',
  'Inverted range,S <= 13 && S >= 14',
  'Always true,S != 13 || S != 14',
  'Numbers 0 and 1 only,Z == 1 || Z == 0',
  'True and 1,Y == true || Y == 1',
  'True and 2,W == true || W == 2',
  'Literal first,150 < T',
  'Literal first only,5 < Q',
  'Single value domain,V > -1',
  'Lower case tag,a == true && A == false',
  `Tabs,${tab}A${tab}==${tab}true${tab}&&${tab}T${tab}>${tab}150${tab}`,
  `No-break space,A == true${nbsp}&& T > 150`,
  `Line separator,A == true${lineSeparator}&& T > 150`,
  `Byte order mark,A == true${bom}&& T > 150`,
  'Duplicate,A == true && T > 150',
  'A == true',
  '',
  '   ',
  'Triple equals,A === B',
  'Assignment,A = 1',
  'Call,foo(A)',
  'Call after paren,(A)(B)',
  'At sign,@A == true',
  'Empty expression,',
  'Dangling operator,A &&',
  'Open paren,(A',
  'Adjacent operands,A B',
  'Adjacent numbers,1 2',
  'Reserved word,if == true',
  'Null,A == null',
  'String,A == \'x\'',
  'Bitwise and,A & B',
  '"Quoted, name",A == true',
  'Trailing spaces,B == true   ',
];
const edgeCsv = edgeLines.map((line, i) => line + (i % 3 === 0 ? '\r\n' : '\n')).join('');

const published = {
  csv: readFileSync(join(repo, 'data', 'expressions.csv'), 'utf8'),
  tags: readFileSync(join(repo, 'data', 'tags.json'), 'utf8'),
};
const scenarios = [
  { name: 'published', csv: published.csv, tags: published.tags, states: 20 },
  { name: 'edge', csv: edgeCsv, tags: null, states: 300 },
];

const work = mkdtempSync(join(tmpdir(), 'jev-style-reference-'));
try {
  writeFileSync(join(work, 'bench-core.mts'), core);
  const output = { generatedBy: 'generate-reference.mjs', benchSha: null, rules, scenarios: [] };
  output.benchSha = execFileSync('git', ['-C', repo, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  for (const scenario of scenarios) {
    rmSync(join(work, 'data'), { recursive: true, force: true });
    mkdirSync(join(work, 'data'));
    writeFileSync(join(work, 'data', 'expressions.csv'), scenario.csv);
    if (scenario.tags !== null) writeFileSync(join(work, 'data', 'tags.json'), scenario.tags);
    const out = join(work, 'out.json');
    execFileSync(process.execPath, ['--experimental-strip-types', '--no-warnings', join(work, 'bench-core.mts')], {
      cwd: work,
      env: { ...process.env, REFERENCE_OUT: out, REFERENCE_STATES: String(scenario.states) },
      stdio: ['ignore', 'ignore', 'inherit'],
    });
    const dump = JSON.parse(readFileSync(out, 'utf8'));
    output.scenarios.push({ name: scenario.name, csv: scenario.csv, tags: scenario.tags, ...dump });
    console.log(`${scenario.name}: ${dump.rows.length} rows, ${dump.skipped.length} skipped, ${dump.literals.length} tags, ${dump.states.length} states`);
  }

  const target = join(dirname(fileURLToPath(import.meta.url)), 'reference.json');
  writeFileSync(target, JSON.stringify(output) + '\n');
  console.log(`wrote ${target}`);
} finally {
  rmSync(work, { recursive: true, force: true });
}
