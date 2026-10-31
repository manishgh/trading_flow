/* Keep uncertain retries on their posted command; edits explicitly create another command. */
(() => {
    const form = document.querySelector('[data-warmup-add-form]');
    const key = form?.elements.namedItem('AddCommandKey');
    if (!form || !key) return;
    // Changing requested inputs means a new intentional add/run, while an unchanged resubmit retains its retry key.
    function edited(event) {
        if (event.target !== key) key.value = crypto.randomUUID();
    }
    form.addEventListener('input', edited);
    form.addEventListener('change', edited);
})();
