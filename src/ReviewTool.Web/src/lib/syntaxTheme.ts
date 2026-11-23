/**
 * Syntax categories used for colouring, and the TextMate scopes that map to them.
 *
 * Shiki colours tokens from a theme. Instead of real colours, this theme gives every category a
 * unique placeholder colour; the renderer turns that back into a CSS class (`tk-<category>`), and the
 * actual colours live in styles.css for light and dark mode. Scopes are the standard TextMate names
 * shared by all VS Code grammars, so this works for every language, not just C#.
 */
export const CATEGORIES = {
  comment: ['comment', 'punctuation.definition.comment', 'string.comment'],
  keyword: [
    'keyword',
    'storage.type',
    'storage.modifier',
    'keyword.type',
    'variable.language',
    'support.type.primitive',
    'keyword.other.unit',
    'markup.heading',
    'entity.name.section',
  ],
  control: ['keyword.control', 'keyword.other.await', 'keyword.operator.new', 'keyword.operator.expression', 'storage.type.function.arrow'],
  operator: [
    'keyword.operator',
    'punctuation.accessor.optional',
    'punctuation.separator.key-value',
  ],
  function: [
    'entity.name.function',
    'support.function',
    'meta.function-call.generic',
    'variable.function',
    'entity.name.function.member',
    'meta.method-call entity.name.function',
    'entity.name.function.decorator',
  ],
  type: [
    'entity.name.type',
    'entity.name.class',
    'entity.name.struct',
    'entity.name.interface',
    'entity.name.enum',
    'entity.other.inherited-class',
    'support.class',
    'support.type',
    'storage.type.class.jsdoc',
    'entity.name.type.alias',
  ],
  namespace: ['entity.name.namespace', 'entity.name.type.namespace', 'entity.name.package', 'entity.name.module', 'storage.modifier.import'],
  variable: ['variable', 'variable.other', 'entity.name.variable', 'variable.other.readwrite', 'meta.definition.variable'],
  parameter: ['variable.parameter', 'entity.name.variable.parameter', 'meta.parameter variable'],
  property: [
    'variable.other.property',
    'variable.other.member',
    'variable.other.object.property',
    'support.variable.property',
    'entity.name.variable.property',
    'entity.name.variable.field',
    'meta.object-literal.key',
    'support.type.property-name',
    'entity.name.tag.yaml',
    'meta.attribute.python',
  ],
  constant: [
    'constant',
    'constant.language',
    'constant.other',
    'variable.other.constant',
    'variable.other.enummember',
    'entity.name.constant',
    'support.constant',
    'markup.underline.link',
  ],
  number: ['constant.numeric'],
  string: ['string', 'punctuation.definition.string', 'markup.inline.raw', 'markup.fenced_code', 'string.quoted.docstring'],
  escape: ['constant.character.escape', 'constant.character', 'constant.other.placeholder', 'punctuation.definition.template-expression', 'punctuation.section.embedded'],
  regex: ['string.regexp'],
  tag: ['entity.name.tag', 'punctuation.definition.tag'],
  // Note: Python tags member access ("obj.name") as meta.attribute, so that generic scope isn't used here.
  attribute: ['entity.other.attribute-name', 'storage.type.annotation', 'meta.decorator', 'punctuation.decorator'],
  punctuation: ['punctuation', 'meta.brace', 'punctuation.terminator', 'punctuation.separator', 'punctuation.accessor'],
  emphasis: ['markup.bold', 'markup.italic'],
} as const;

export type Category = keyof typeof CATEGORIES;

const names = Object.keys(CATEGORIES) as Category[];

/** Placeholder colour for each category (distinct, never shown). */
const placeholder = (i: number) => `#${(0x100000 + i + 1).toString(16).padStart(6, '0')}`;

/** Placeholder colour (lower-case) → category. */
export const COLOR_TO_CATEGORY: ReadonlyMap<string, Category> = new Map(names.map((n, i) => [placeholder(i), n]));

/** Categories whose content should never get bracket colours (brackets inside text aren't code). */
export const TEXTUAL: ReadonlySet<Category> = new Set<Category>(['comment', 'string', 'regex', 'escape']);

/** A TextMate theme whose "colours" are the category placeholders. */
export const reviewToolTheme = {
  name: 'reviewtool',
  type: 'dark' as const,
  colors: { 'editor.foreground': '#000000', 'editor.background': '#ffffff' },
  fg: '#000000',
  bg: '#ffffff',
  settings: [
    { settings: { foreground: '#000000', background: '#ffffff' } },
    ...names.map((n, i) => ({ scope: [...CATEGORIES[n]], settings: { foreground: placeholder(i) } })),
  ],
};
