import type { HelpArticle } from './articles';

const normalize = (value: string) => value.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, ' ').trim();

export function searchHelpArticles(articles: HelpArticle[], query: string): HelpArticle[] {
  const terms = normalize(query).split(' ').filter(Boolean);
  if (!terms.length) return articles;
  return articles.map(article => {
    const title = normalize(article.title);
    const aliases = normalize(article.aliases.join(' '));
    const signatures = normalize(article.exactSignatures.join(' '));
    const body = normalize([article.plainLanguage, ...article.likelyCauses, ...article.safeSteps].join(' '));
    const matches = terms.filter(term => title.includes(term) || aliases.includes(term) || signatures.includes(term) || body.includes(term));
    const score = matches.length * 10 + terms.reduce((total, term) => total + (title.includes(term) ? 5 : 0) + (aliases.includes(term) ? 3 : 0) + (signatures.includes(term) ? 2 : 0), 0);
    return { article, score, complete: matches.length === terms.length };
  }).filter(result => result.complete).sort((left, right) => right.score - left.score || left.article.title.localeCompare(right.article.title)).map(result => result.article);
}
