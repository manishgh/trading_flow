(() => {
    const panel = document.querySelector('[data-company-assessment-panel]');
    if (!panel) return;
    const button = panel.querySelector('[data-load-company-assessments]');
    const status = panel.querySelector('[data-company-assessment-status]');
    const summary = panel.querySelector('[data-company-assessment-summary]');
    const samples = panel.querySelector('[data-company-assessment-samples]');
    const lifetime = new AbortController();
    // Clear private archived samples on departure, including pages retained in browser history.
    window.addEventListener('pagehide', () => { lifetime.abort(); summary.replaceChildren(); samples.replaceChildren(); status.textContent = ''; });
    window.addEventListener('pageshow', event => { if (event.persisted) location.reload(); });
    // Treat provider text as text; no article content or model explanation is executable markup.
    function paragraph(parent, text) {
        const row = document.createElement('p'); row.textContent = text; parent.append(row);
    }
    // Fetch already published measurements only on request, without collecting news or running a model.
    button.addEventListener('click', async () => {
        button.disabled = true; summary.replaceChildren(); samples.replaceChildren(); status.textContent = 'Verifying published assessment samples…';
        try {
            const response = await fetch('/api/v1/news/company-assessments', { credentials: 'same-origin', signal: lifetime.signal });
            if (!response.ok) throw new Error(response.status === 401 ? 'Sign in to review archived assessments.' : `Assessment request failed (${response.status}).`);
            const result = await response.json();
            if (lifetime.signal.aborted) return;
            status.textContent = result.message;
            if (result.summary) {
                paragraph(summary, `Model: ${result.summary.model}@${result.summary.modelRevision}`);
                paragraph(summary, `Actually assessed UTC: ${result.summary.assessedFromUtc} through ${result.summary.assessedThroughUtc}`);
                paragraph(summary, 'Classifier accuracy has not been measured. Model confidence is not the chance of a profitable trade.');
            }
            for (const item of result.samples) {
                const article = document.createElement('article'); article.className = 'news-entry';
                const heading = document.createElement('strong'); heading.textContent = `${item.symbol}: ${item.headline}`; article.append(heading);
                paragraph(article, `${item.companyDirection} · ${item.category} · ${item.textSupport}`);
                paragraph(article, `Text tone: ${item.textTone || 'Not assessed'}${item.modelConfidence == null ? '' : ` · model confidence ${item.modelConfidence}`}`);
                paragraph(article, item.explanation || 'Supported company assertion and model polarity agree.');
                if (item.companyName) paragraph(article, `Company name: ${item.companyName} · ${item.companyNameProvider} capture UTC: ${item.companyNameObservedAtUtc}. This capture does not prove the name was known at the old trade time.`);
                paragraph(article, `Published UTC: ${item.publishedAtUtc || 'Unknown'} · Actually assessed UTC: ${item.assessedAtUtc}`);
                const evidence = document.createElement('details'); const title = document.createElement('summary'); title.textContent = 'Exact assessment text'; evidence.append(title);
                paragraph(evidence, item.evidenceText); article.append(evidence); samples.append(article);
            }
        } catch (error) { if (!lifetime.signal.aborted) status.textContent = error.message; }
        finally { if (!lifetime.signal.aborted) button.disabled = false; }
    });
})();
