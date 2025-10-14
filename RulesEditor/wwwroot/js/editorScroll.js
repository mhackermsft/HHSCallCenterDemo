export function scrollToNode(nodeId) {
    try {
        const container = document.querySelector('.flow-container');
        if (!container) return;
        const node = container.querySelector(`[data-node-id="${nodeId}"]`);
        if (!node) return;

        const containerRect = container.getBoundingClientRect();
        const nodeRect = node.getBoundingClientRect();

        // Calculate the offsets needed to center the node inside the scrollable container
        const scrollLeft = container.scrollLeft + (nodeRect.left - containerRect.left) - (container.clientWidth / 2) + (nodeRect.width / 2);
        const scrollTop = container.scrollTop + (nodeRect.top - containerRect.top) - (container.clientHeight / 2) + (nodeRect.height / 2);

        container.scrollTo({ left: scrollLeft, top: scrollTop, behavior: 'smooth' });
    } catch (e) {
        console.warn('scrollToNode failed', e);
    }
}