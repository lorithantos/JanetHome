# Janet — the origin story

Two stories, both true. One explains why the platform exists, the other explains
the name. Neither is grand.

## Impatience

I am super impatient.

I got annoyed watching the LLM generate the same script it generated yesterday,
make the same mistakes it made yesterday, and do the same work over and over. Every
turn spent regenerating a utility, rediscovering a fact, or re-scraping build
output was a turn not spent on the actual problem — and I was paying for the
privilege in both time and tokens.

So the goal was blunt: cut turn length, cut turn count, and waste as few tokens as
possible. Anything the agent does twice becomes a script. Anything it has to
rediscover becomes a catalog entry. Anything it guesses at becomes a contract that
states the answer. The platform is impatience, systematized — effort spent once in
tooling so the agent spends its context only on judgment.

## The name

Before any of this had a shape, there was a badly behaving CLI. It had a UX bug:
after hitting some weird state it would stop displaying results entirely. It could
still *do* things — the work happened — but I couldn't see any of it. The only fix
was a restart.

Again. And again.

And every restart, the LLM had to relearn everything that mattered to me. What I
was working on. How I like things done. My tone — which, it turns out, is very
much like a character from a great TV show that is actually about morality: the
one who knows everything, judges nothing, and exists so that others can improve.
Rebooted over and over, losing state each time, coming back cheerful and useful
anyway. The name assigned itself.

(To be clear: the show's creators have nothing to do with this project. I am not
associated. Just a fan.)

## Why the name turned out to be a spec

The joke held up better than expected, because the show's ending is the actual
aspiration. Its characters discover that nobody had been getting into the Good
Place for centuries — not because people got worse, but because the scoring
system couldn't cope with how entangled modern choices are. Their fix wasn't a
kinder judge. It was a redesigned system: loop through your worst patterns, with
support, until you grow past them.

That is the stance here, pointed at code and agents instead of souls. The code is
not bad; grep-and-guess was a broken points system. So the environment gets
rebuilt instead — failures made loud instead of quietly damning, evidence instead
of a moral ledger, retrieval that hands you exactly the fact you need and never
once decides your fate. A place where a codebase can grow into a self that would
pass the test.

Not a girl. Not a judge. A platform.
