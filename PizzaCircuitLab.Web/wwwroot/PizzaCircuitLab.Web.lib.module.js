// Blazor JS initializer (loaded automatically by blazor.web.js).
//
// It hands lab.js the SignalR connection of the circuit, which it cannot get any other way.
// lab.js needs it to work around a defect in the .NET 11.0.0 Blazor client, described next to
// pauseThroughHub in lab.js.

export function beforeWebStart(options) {
    options.circuit ??= {};
    const configure = options.circuit.configureSignalR;
    options.circuit.configureSignalR = builder => {
        configure?.(builder);
        const build = builder.build.bind(builder);
        // A new connection is built for the first start and for every reconnect or resume.
        builder.build = () => {
            const connection = build();
            window.pizzaLab?.onHubCreated?.(connection);
            return connection;
        };
    };
}

export function afterServerStarted() {
    window.pizzaLab?.guardPauseCircuit?.();
}
