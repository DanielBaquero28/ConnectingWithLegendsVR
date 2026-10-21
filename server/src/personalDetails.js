// Spec 12 Part R — personal-detail guard on character replies.
//
// The model is told not to repeat names, schools or places the student
// mentions (SAFETY_PREAMBLE + TURN_SUFFIX), but that is a request, not a
// guarantee. This is the server-side backstop: it pulls the 1–3 words that
// follow a self-introduction phrase in the student's turns of this session
// ("my name is Daniel", "I go to Westside High") and removes them from the
// reply before TTS and before the reply is stored.
//
// Nothing extracted here is stored or logged: the details exist only as a
// local list for the duration of one request.

// The phrases that introduce a personal detail (case-insensitive). Curly and
// straight apostrophes both count, and "im" covers typed "Im called".
const TRIGGER_RE = /\b(?:my name is|my name['’]s|i['’]?m called|i am called|call me|i go to|my school is|i live in)\b/giu;

const MAX_DETAIL_WORDS = 3;

// Skipped when they come first ("I live in the Bronx" -> "Bronx").
const LEADING_ARTICLES = new Set(['the', 'a', 'an']);

// End the capture: function words, fillers and common verbs that follow a
// detail rather than belong to it ("my name is Daniel and I ...").
const STOP_WORDS = new Set([
  'and', 'but', 'or', 'so', 'because', 'cause', 'if', 'then', 'than', 'though',
  'i', 'im', 'me', 'my', 'mine', 'we', 'us', 'our', 'you', 'your', 'he', 'him', 'his',
  'she', 'her', 'it', 'its', 'they', 'them', 'their', 'this', 'that', 'these', 'those',
  'is', 'am', 'are', 'was', 'were', 'be', 'been', 'being', 'do', 'does', 'did',
  'have', 'has', 'had', 'can', 'could', 'will', 'would', 'should', 'may', 'might', 'must',
  'to', 'in', 'at', 'on', 'from', 'with', 'for', 'of', 'by', 'near', 'about', 'into',
  'like', 'um', 'uh', 'er', 'well', 'really', 'actually', 'too', 'also', 'now', 'here',
  'there', 'please', 'thanks', 'thank', 'okay', 'ok', 'yeah', 'yes', 'no', 'not', 'very',
  'just', 'what', 'who', 'whom', 'how', 'why', 'where', 'when', 'which',
  'tell', 'know', 'think', 'want', 'wanted', 'ask', 'asking', 'wonder', 'go', 'going',
  'every', 'each', 'all', 'some', 'any', 'later', 'back', 'maybe', 'sometimes', 'today',
]);

// Kept inside a captured phrase (so "Westside High School" is removed whole)
// but never removed on their own: "school", "new" or "great" are everyday
// words in a history answer.
const GENERIC_WORDS = new Set([
  'school', 'high', 'middle', 'elementary', 'primary', 'secondary', 'junior', 'senior',
  'academy', 'college', 'university', 'prep', 'institute', 'charter', 'public', 'private',
  'city', 'town', 'village', 'county', 'state', 'country', 'street', 'road', 'avenue',
  'new', 'old', 'north', 'south', 'east', 'west', 'northern', 'southern', 'eastern', 'western',
  'saint', 'st', 'san', 'santa', 'los', 'las', 'la', 'el', 'de', 'del', 'fort', 'mount',
  'lake', 'port', 'upper', 'lower', 'little', 'big', 'small', 'great', 'united', 'states',
  'kingdom', 'republic', 'district', 'area', 'place', 'home', 'house',
  // everyday destinations after "I go to": "the library", "church", "bed"
  'library', 'museum', 'park', 'gym', 'church', 'mosque', 'temple', 'synagogue', 'bed',
  'sleep', 'work', 'class', 'classes', 'camp', 'beach', 'store', 'shop', 'mall', 'market',
  'practice', 'lessons', 'hospital', 'office', 'farm', 'capital',
]);

// A preposition directly before a removed detail goes with it:
// "How is life in Boston?" -> "How is life?"
const LEADING_PREPOSITIONS = ['in', 'at', 'from', 'to'];

const WORD_RE = /^\s*["'“‘(]*([\p{L}\p{M}][\p{L}\p{M}'’-]*)([^\s]*)/u;

function normalize(word) {
  return word.toLowerCase().replace(/['’]/g, '');
}

// Returns the captured details as arrays of words, e.g.
// [['Daniel'], ['Westside', 'High', 'School']]. `texts` are the student's
// turns, oldest first; order does not matter.
function extractDetails(texts) {
  const details = [];
  for (const text of texts) {
    if (typeof text !== 'string' || !text) continue;
    TRIGGER_RE.lastIndex = 0;
    let match;
    while ((match = TRIGGER_RE.exec(text)) !== null) {
      let rest = text.slice(match.index + match[0].length);
      const words = [];
      while (words.length < MAX_DETAIL_WORDS) {
        const m = WORD_RE.exec(rest);
        if (!m) break;
        // Possessive "Daniel's" is still Daniel.
        const word = m[1].replace(/['’]s$/iu, '').replace(/['’-]+$/u, '');
        const key = normalize(word);
        if (STOP_WORDS.has(key)) break;
        if (!(words.length === 0 && LEADING_ARTICLES.has(key)) && word) words.push(word);
        rest = rest.slice(m[0].length);
        if (m[2]) break; // punctuation after the word ends the detail
      }
      if (words.length > 0) details.push(words);
    }
  }
  return details;
}

function escapeRe(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// One removal pattern: an optional leading preposition, the words (any
// spacing), an optional possessive, all on whole-word boundaries.
function removalPattern(words) {
  const body = words.map(escapeRe).join('\\s+');
  const prep = `(?:\\b(?:${LEADING_PREPOSITIONS.join('|')})\\s+)?`;
  return new RegExp(`${prep}(?<![\\p{L}\\p{M}\\p{N}])${body}(?:['’]s)?(?![\\p{L}\\p{M}\\p{N}])`, 'giu');
}

// Repairs what removal leaves behind: doubled spaces, a space or comma
// before end punctuation, a comma opening a sentence, a lowercase sentence
// start. "I'm glad to meet you, ." -> "I'm glad to meet you."
function tidy(text) {
  return text
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/\s+([,.!?;:])/g, '$1')
    .replace(/[,;:]+(?=[,;:.!?])/g, '')
    .replace(/(^|[.!?]\s)[,;:]\s*/g, '$1')
    .replace(/([.!?])[.!?]+/g, '$1')
    .replace(/(^|[.!?]\s)(\p{Ll})/gu, (_, lead, ch) => lead + ch.toUpperCase())
    .trim();
}

// Removes the student's details from `reply`. Words of the character's own
// name are never removed ("I go to Lincoln High" must not turn Abraham
// Lincoln into Abraham), and generic words only go as part of a whole phrase.
// Returns { text, removed } where removed counts the cuts made.
function scrubReply(reply, details, protectedNames) {
  if (!reply || !details || details.length === 0) return { text: reply, removed: 0 };

  const protectedWords = new Set();
  for (const name of protectedNames || []) {
    for (const w of String(name || '').split(/[\s_.-]+/)) if (w) protectedWords.add(normalize(w));
  }
  const removable = (w) => !GENERIC_WORDS.has(normalize(w)) && !protectedWords.has(normalize(w));

  // Whole phrases first (longest first), then single words. A phrase goes
  // whole even when its only specific word is the character's own name
  // ("Lincoln High" in Lincoln's reply); an all-generic one ("small town") never.
  const phrases = new Map();
  const singles = new Map();
  for (const words of details) {
    if (words.length > 1 && words.some((w) => !GENERIC_WORDS.has(normalize(w)))) {
      phrases.set(words.map(normalize).join(' '), words);
    }
    for (const w of words) if (removable(w)) singles.set(normalize(w), [w]);
  }
  const patterns = [...phrases.values()]
    .sort((a, b) => b.length - a.length)
    .concat([...singles.values()])
    .map(removalPattern);

  let text = reply;
  let removed = 0;
  for (const re of patterns) {
    text = text.replace(re, () => { removed++; return ' '; });
  }
  return removed > 0 ? { text: tidy(text), removed } : { text: reply, removed: 0 };
}

module.exports = { extractDetails, scrubReply, tidy };
