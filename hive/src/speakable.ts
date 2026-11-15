// speakable(): Beeswax's lines as the TTS should hear them (Rome's 04:00 direction: conversational, never shouting).
// Captions keep their written form; only the text sent to ElevenLabs changes. ElevenLabs reads CAPS, "!!", "?!" and
// "..." as shouting or long dramatic pauses, and stops on every full stop of "Don't. Say. That.".

/** All-caps words that are real acronyms and stay as they are. */
const ACRONYMS = new Set(["US", "USA", "UK", "EU", "UN", "OK", "TV", "AI", "AR", "VR", "XR", "MR", "DNA", "NASA", "CO2", "UV", "GPS", "ID"]);

function capFirst(w: string): string {
  return w.charAt(0).toUpperCase() + w.slice(1);
}

/** One line of speech, softened for TTS: no CAPS words (acronyms stay), at most one "!", no "...", no staccato. */
export function speakable(text: string): string {
  let s = text;
  // "..." and "…" are long pauses: a comma, or nothing at the start of the line
  s = s.replace(/^\s*(?:\.{3,}|…)\s*/, "");
  s = s.replace(/\s*(?:\.{3,}|…)\s*(?=[.!?,;:]|$)/g, "");
  s = s.replace(/\s*(?:\.{3,}|…)\s*/g, ", ");
  s = s.replace(/([.!?;:])\s*,\s*/g, "$1 "); // "go? ...Your" -> "go? Your"
  // "?!", "!?", "!!": one mark
  s = s.replace(/[!?]*\?[!?]*!+[!?]*|[!?]*![!?]*\?[!?]*/g, "?");
  s = s.replace(/!{2,}/g, "!");
  // at most one "!": the rest become full stops
  let bangs = 0;
  s = s.replace(/!/g, () => (bangs++ === 0 ? "!" : "."));
  // ALL-CAPS words (2+ letters) to lower case, keeping acronyms; a word that starts a sentence keeps a capital
  s = s.replace(/\b[A-Z][A-Z']*[A-Z]\b/g, (w, at: number) => {
    if (ACRONYMS.has(w)) return w;
    const before = s.slice(0, at);
    const lower = w.toLowerCase();
    return /^\W*$/.test(before) || /[.!?]["'”)]?\s+$/.test(before) ? capFirst(lower) : lower;
  });
  // staccato: three or more one-word sentences in a row ("Don't. Say. That.") become one sentence with commas
  s = s.replace(/(?<=^|[.!?] )(?:[A-Za-z][\w']{0,7}\. ){2,}[A-Za-z][\w']{0,7}\.(?=\s|$)/g, (run) => {
    const words = run.split(". ").map((w) => w.replace(/\.$/, ""));
    return words.map((w, i) => (i === 0 || w === "I" ? w : w.charAt(0).toLowerCase() + w.slice(1))).join(", ") + ".";
  });
  return s.replace(/\s+/g, " ").replace(/\s+([,.!?])/g, "$1").replace(/,{2,}/g, ",").replace(/,([.!?])/g, "$1").trim();
}
