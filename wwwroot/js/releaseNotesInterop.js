window.releaseNotes = {
    isNarrowViewport: (maxWidth) => {
        const width = Number.isFinite(maxWidth) ? maxWidth : 991;
        return window.matchMedia(`(max-width: ${width}px)`).matches;
    },

    highlightCodeBlocks: () => {
        if (!window.hljs) {
            return;
        }

        const codeBlocks = Array.from(document.querySelectorAll('.combined-content pre code, .combined-content code'));
        if (codeBlocks.length > 0) {
            codeBlocks.forEach((block) => {
                try {
                    hljs.highlightElement(block);
                } catch {
                    // ignore individual highlight errors
                }
            });
        } else if (typeof hljs.highlightAll === 'function') {
            try {
                hljs.highlightAll();
            } catch {
                // ignore
            }
        }
    },

    scrollToHeading: (headingId) => {
        if (!headingId) {
            return;
        }

        const target = document.getElementById(headingId);
        if (target) {
            openParentDetails(target);
            const topOffset = getTopbarHeight() + 12;
            const targetTop = target.getBoundingClientRect().top + window.scrollY;
            window.scrollTo({
                top: Math.max(targetTop - topOffset, 0),
                behavior: 'smooth'
            });

            try {
                const currentHash = (window.location.hash || '').replace(/^#/, '');
                if (currentHash !== headingId) {
                    const currentPathWithQuery = `${window.location.pathname || ''}${window.location.search || ''}`;
                    history.replaceState(null, '', `${currentPathWithQuery}#${encodeURIComponent(headingId)}`);
                }
            } catch {
                // ignore hash update failures
            }
        }
    },

    scrollToTop: () => {
        window.scrollTo({ top: 0, behavior: 'smooth' });
    },

    initScrollSpy: (dotNetRef, navSelector, headingSelector) => {
        try {
            if (window._releaseNotesScrollSpy && Array.isArray(window._releaseNotesScrollSpy.listeners)) {
                window._releaseNotesScrollSpy.listeners.forEach(({ target, type, handler }) => target.removeEventListener(type, handler));
            }

            const getNavLinks = () => Array.from(document.querySelectorAll(navSelector));
            const getHeadings = () => Array.from(document.querySelectorAll(headingSelector));

            const syncTopbarOffset = () => {
                const topOffset = Math.max(getTopbarBottom(), 0);
                document.documentElement.style.setProperty('--release-topbar-offset', `${Math.max(topOffset, 0)}px`);
            };

            const ensureHeadingPermalinks = () => {
                const headings = getHeadings();
                headings.forEach((heading) => {
                    if (!heading || !heading.id) {
                        return;
                    }

                    heading.classList.add('has-heading-anchor');

                    if (heading.querySelector(':scope > a.heading-anchor')) {
                        return;
                    }

                    const anchor = document.createElement('a');
                    anchor.className = 'heading-anchor';
                    const currentPathWithQuery = `${window.location.pathname || ''}${window.location.search || ''}`;
                    anchor.href = `${currentPathWithQuery}#${encodeURIComponent(heading.id)}`;
                    anchor.setAttribute('aria-label', `Link to section ${heading.textContent?.trim() || heading.id}`);
                    anchor.textContent = '#';
                    anchor.addEventListener('click', (event) => {
                        event.preventDefault();
                        window.releaseNotes.scrollToHeading(heading.id);
                    });
                    heading.appendChild(anchor);
                });
            };

            const scrollToCurrentHash = () => {
                const hash = window.location.hash || '';
                if (!hash.startsWith('#') || hash.length <= 1) {
                    return;
                }

                const headingId = decodeURIComponent(hash.substring(1));
                if (!headingId) {
                    return;
                }

                const target = document.getElementById(headingId);
                if (!target) {
                    return;
                }

                openParentDetails(target);
                const topOffset = getTopbarHeight() + 12;
                const targetTop = target.getBoundingClientRect().top + window.scrollY;
                window.scrollTo({ top: Math.max(targetTop - topOffset, 0), behavior: 'auto' });
            };

            ensureHeadingPermalinks();
            syncTopbarOffset();

            let ticking = false;
            let activeId = '';
            let showToTop = false;

            const activateLink = (currentId) => {
                if (!currentId || currentId === activeId) {
                    return false;
                }

                activeId = currentId;
                const currentNormalized = currentId.toLowerCase();
                const currentLinks = getNavLinks();

                currentLinks.forEach(link => {
                    const targetId = (link.getAttribute('data-id') || '').trim();
                    if (targetId && targetId.toLowerCase() === currentNormalized) {
                        link.classList.add('active');
                    } else {
                        link.classList.remove('active');
                    }
                });

                return true;
            };

            const onScroll = () => {
                if (ticking) return;
                ticking = true;
                requestAnimationFrame(() => {
                    syncTopbarOffset();

                    const threshold = Math.max(window.innerHeight * 0.01, 8);
                    const shouldShowToTop = window.scrollY > threshold;
                    if (shouldShowToTop !== showToTop && dotNetRef && typeof dotNetRef.invokeMethodAsync === 'function') {
                        showToTop = shouldShowToTop;
                        dotNetRef.invokeMethodAsync('NotifyScrollTopVisibility', shouldShowToTop).catch(() => {});
                    }

                    const headings = getHeadings().filter((heading) => heading.offsetParent !== null);
                    if (!getNavLinks().length || !headings.length) {
                        ticking = false;
                        return;
                    }

                    let current = null;
                    const offset = getTopbarHeight() + 20;
                    for (let i = 0; i < headings.length; i++) {
                        const r = headings[i].getBoundingClientRect();
                        if (r.top - offset <= 0) {
                            current = headings[i];
                        } else {
                            break;
                        }
                    }

                    if (!current) current = headings[0];

                    const currentId = current.id;
                    const changed = activateLink(currentId);
                    if (changed && dotNetRef && typeof dotNetRef.invokeMethodAsync === 'function') {
                        dotNetRef.invokeMethodAsync('NotifyActiveHeading', currentId).catch(() => {});
                    }
                    ticking = false;
                });
            };

            onScroll();
            scrollToCurrentHash();

            const onHashChange = () => {
                syncTopbarOffset();
                scrollToCurrentHash();
            };

            const onResize = () => {
                syncTopbarOffset();
            };

            const onNavClick = (event) => {
                const link = event.target && event.target.closest ? event.target.closest(navSelector) : null;
                if (!link) {
                    return;
                }

                const href = link.getAttribute('href') || '';
                const dataId = (link.getAttribute('data-id') || '').trim();
                let headingId = dataId;

                if (!headingId && href.includes('#')) {
                    const hashPart = href.substring(href.indexOf('#') + 1);
                    headingId = decodeURIComponent(hashPart);
                }

                if (!headingId) {
                    return;
                }

                event.preventDefault();
                window.releaseNotes.scrollToHeading(headingId);
            };

            const listeners = [
                { target: window, type: 'scroll', handler: onScroll, options: { passive: true } },
                { target: window, type: 'hashchange', handler: onHashChange },
                { target: window, type: 'resize', handler: onResize, options: { passive: true } },
                { target: document, type: 'click', handler: onNavClick }
            ];
            listeners.forEach(({ target, type, handler, options }) => target.addEventListener(type, handler, options));
            window._releaseNotesScrollSpy = { listeners };
        } catch (e) {
            console.warn('initScrollSpy error', e);
        }
    }
};

const getTopbarRect = () => document.querySelector('.release-topbar')?.getBoundingClientRect();
const getTopbarHeight = () => getTopbarRect()?.height ?? 0;
const getTopbarBottom = () => getTopbarRect()?.bottom ?? 0;

const openParentDetails = (element) => {
    let parent = element?.parentElement;
    while (parent) {
        if (parent.tagName === 'DETAILS') {
            parent.open = true;
        }
        parent = parent.parentElement;
    }
};

window.releaseNotesCache = {
    get: (key) => {
        try {
            return window.localStorage.getItem(key);
        } catch {
            return null;
        }
    },

    set: (key, value) => {
        try {
            window.localStorage.setItem(key, value);
        } catch {
            // ignore storage failures
        }
    },

    remove: (key) => {
        try {
            window.localStorage.removeItem(key);
        } catch {
            // ignore storage failures
        }
    }
};

window.releaseNotesTheme = (() => {
    let mediaQuery = null;
    let mediaHandler = null;
    let currentMode = 'auto';

    const getResolvedTheme = (mode) => {
        if (mode === 'light' || mode === 'dark') {
            return mode;
        }

        if (!mediaQuery) {
            mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
        }

        return mediaQuery.matches ? 'dark' : 'light';
    };

    const applyInternal = (mode) => {
        currentMode = mode === 'light' || mode === 'dark' ? mode : 'auto';

        const resolved = getResolvedTheme(currentMode);
        document.documentElement.setAttribute('data-bs-theme', resolved);
        document.documentElement.style.colorScheme = currentMode === 'auto' ? 'light dark' : resolved;

        if (!mediaQuery) {
            mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
        }

        if (mediaHandler) {
            mediaQuery.removeEventListener('change', mediaHandler);
            mediaHandler = null;
        }

        if (currentMode === 'auto') {
            mediaHandler = () => applyInternal('auto');
            mediaQuery.addEventListener('change', mediaHandler);
        }
    };

    return {
        apply: (mode) => applyInternal(mode)
    };
})();
