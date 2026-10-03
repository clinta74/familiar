// Generates the clients' protocol types from protocol/familiar.schema.json (docs/MOBILE.md §6): the
// web client's TypeScript and the phone apps' Kotlin. The schema is generated from the server's
// records (src/Familiar.Server/Protocol/ProtocolSchema.cs), so the three can't drift.
//
//   npm run protocol         write the generated files
//   npm run protocol:check   fail if they're out of date (CI)
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const schemaPath = join(root, 'protocol', 'familiar.schema.json');
const tsPath = join(root, 'client', 'src', 'protocol', 'generated.ts');
const ktPath = join(root, 'mobile', 'shared', 'src', 'commonMain', 'kotlin', 'app', 'familiar', 'shared', 'protocol', 'Generated.kt');

const schema = JSON.parse(readFileSync(schemaPath, 'utf8'));
const defs = schema.$defs;
const names = Object.keys(defs).sort();
const events = Object.entries(schema['x-events']);
const isEnum = (name) => defs[name].enum !== undefined;

const header = (comment) =>
  `${comment} Generated from protocol/familiar.schema.json by client/scripts/protocol.mjs - don't edit.\n` +
  `${comment} To change it, change the server's records, then regenerate (see ProtocolSchemaTests).\n`;

/** A property's schema: its referenced definition (if any), whether it's nullable, and the rest. */
function shape(property) {
  if (property.anyOf) {
    const ref = property.anyOf.find((p) => p.$ref);
    return { ...shape(ref), nullable: property.anyOf.some((p) => p.type === 'null') };
  }
  const types = [property.type].flat().filter(Boolean);
  return {
    ref: property.$ref?.replace('#/$defs/', ''),
    type: types.find((t) => t !== 'null'),
    nullable: types.includes('null'),
    items: property.items,
    format: property.format,
    values: property.enum,
  };
}

/** Words wrapped to a width, for doc comments. */
function wrap(text, width) {
  const lines = [];
  let line = '';
  for (const word of text.split(' ')) {
    if (line && line.length + word.length + 1 > width) {
      lines.push(line);
      line = word;
    } else {
      line = line ? `${line} ${word}` : word;
    }
  }
  if (line) lines.push(line);
  return lines;
}

function doc(text, indent) {
  if (!text) return '';
  const safe = text.replaceAll('*/', '*\\/');
  const lines = wrap(safe, 100 - indent.length - 3);
  return lines.length === 1
    ? `${indent}/** ${lines[0]} */\n`
    : `${indent}/**\n${lines.map((l) => `${indent} * ${l}`).join('\n')}\n${indent} */\n`;
}

// --- TypeScript ---------------------------------------------------------------------------------

function tsType(property) {
  const { ref, type, nullable, items, values } = shape(property);
  let t;
  if (ref) t = ref;
  else if (type === 'array') t = `${tsType(items)}[]`;
  // A string with fixed values (the model a familiar is drawn as): just those.
  else if (type === 'string' && values) t = values.map((v) => `'${v}'`).join(' | ');
  else if (type === 'string') t = 'string';
  else if (type === 'integer' || type === 'number') t = 'number';
  else if (type === 'boolean') t = 'boolean';
  else if (type === 'object') t = 'Record<string, unknown>';
  else throw new Error(`No TypeScript type for ${JSON.stringify(property)}`);
  return nullable ? `${t} | null` : t;
}

function typescript() {
  let out = header('//') + '\n';
  for (const name of names) {
    const def = defs[name];
    out += doc(def.description, '');
    if (isEnum(name)) {
      out += `export type ${name} = ${def.enum.map((v) => `'${v}'`).join(' | ')};\n\n`;
      continue;
    }
    out += `export interface ${name} {\n`;
    for (const [prop, property] of Object.entries(def.properties)) {
      // Optional: a record parameter with a default, which a request may leave out.
      const optional = !def.required.includes(prop);
      out += doc(property.description, '  ');
      out += `  ${prop}${optional ? '?' : ''}: ${tsType(property)};\n`;
    }
    out += '}\n\n';
  }
  out += '/** Each event on the conversation stream and its payload, by its SSE event name (null: none). */\n';
  out += 'export interface EventPayloads {\n';
  for (const [event, { payload }] of events) out += `  '${event}': ${payload ?? 'null'};\n`;
  out += '}\n\n';
  out += 'export type EventName = keyof EventPayloads;\n\n';
  out += '/** The stream\'s event names, as the server calls them (EventTypes). */\n';
  out += 'export const EventTypes = {\n';
  for (const [event, { constant }] of events) out += `  ${constant}: '${event}',\n`;
  out += '} as const satisfies Record<string, EventName>;\n';
  return out;
}

// --- Kotlin -------------------------------------------------------------------------------------

function ktType(property) {
  const { ref, type, nullable, items } = shape(property);
  let t;
  // Enums are their names as strings: an app a version behind still reads a value it doesn't know.
  if (ref) t = isEnum(ref) ? 'String' : ref;
  else if (type === 'array') t = `List<${ktType(items)}>`;
  else if (type === 'string') t = 'String';
  else if (type === 'integer') t = 'Int';
  else if (type === 'number') t = 'Double';
  else if (type === 'boolean') t = 'Boolean';
  else if (type === 'object') t = 'JsonObject';
  else throw new Error(`No Kotlin type for ${JSON.stringify(property)}`);
  return nullable ? `${t}?` : t;
}

/** The server's default for a parameter a request may leave out; null for anything nullable. */
function ktDefault(property, type) {
  const value = property.default;
  if (value !== undefined && value !== null) {
    if (typeof value === 'string') return ` = "${value}"`;
    if (type.startsWith('Double')) return ` = ${Number.isInteger(value) ? `${value}.0` : value}`;
    return ` = ${value}`;
  }
  return type.endsWith('?') ? ' = null' : '';
}

const constantName = (value) =>
  value.replace(/([a-z0-9])([A-Z])/g, '$1_$2').replace(/[^A-Za-z0-9]+/g, '_').toUpperCase();

function kotlin() {
  let out = header('//') + '\n';
  out += 'package app.familiar.shared.protocol\n\n';
  out += 'import kotlinx.serialization.Serializable\n';
  out += 'import kotlinx.serialization.json.JsonObject\n\n';
  for (const name of names) {
    const def = defs[name];
    out += doc(def.description, '');
    if (isEnum(name)) {
      out += `object ${name} {\n`;
      for (const value of def.enum) out += `    const val ${constantName(value)} = "${value}"\n`;
      out += '}\n\n';
      continue;
    }
    out += '@Serializable\n';
    out += `data class ${name}(\n`;
    for (const [prop, property] of Object.entries(def.properties)) {
      const type = ktType(property);
      out += doc(property.description, '    ');
      out += `    val ${prop}: ${type}${ktDefault(property, type)},\n`;
    }
    out += ')\n\n';
  }
  out += '/** The stream\'s event names, as the server calls them (EventTypes). */\n';
  out += 'object EventTypes {\n';
  for (const [event, { constant }] of events) out += `    const val ${constantName(constant)} = "${event}"\n`;
  out += '}\n';
  return out;
}

// --- write, or check ----------------------------------------------------------------------------

const outputs = [
  [tsPath, typescript()],
  [ktPath, kotlin()],
];

if (process.argv.includes('--check')) {
  const stale = outputs.filter(([path, text]) => {
    try {
      return readFileSync(path, 'utf8').replaceAll('\r\n', '\n') !== text;
    } catch {
      return true;
    }
  });
  if (stale.length > 0) {
    console.error(`Out of date with protocol/familiar.schema.json: ${stale.map(([p]) => relative(root, p)).join(', ')}. Run npm run protocol.`);
    process.exit(1);
  }
  console.log('protocol: generated types are current');
} else {
  for (const [path, text] of outputs) writeFileSync(path, text);
  console.log(`protocol: wrote ${outputs.map(([p]) => relative(root, p)).join(', ')}`);
}
