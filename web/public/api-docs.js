document.querySelectorAll('[data-tabs]').forEach((tabs) => {
  tabs.querySelectorAll('[data-tab]').forEach((button) => button.addEventListener('click', () => {
    tabs.querySelectorAll('[data-tab]').forEach((b) => b.classList.toggle('active', b === button))
    tabs.querySelectorAll('[data-panel]').forEach((p) => { p.hidden = p.dataset.panel !== button.dataset.tab })
  }))
})
document.querySelectorAll('pre .copy').forEach((button) => button.addEventListener('click', async () => {
  const text = [...button.parentElement.childNodes].filter((n) => n !== button).map((n) => n.textContent).join('').trim()
  await navigator.clipboard.writeText(text)
  button.textContent = 'Copied'; setTimeout(() => { button.textContent = 'Copy' }, 1500)
}))
