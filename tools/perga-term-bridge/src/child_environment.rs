use portable_pty::CommandBuilder;

const RESERVED_PREFIXES: &[&str] = &[
    "PERGA_",
    "PERGA:",
    "ASPNETCORE_",
    "DOTNET_PERGA_",
    "DOTNET_PERGA:",
];

const RESERVED_NAMES: &[&str] = &[
    "DOTNET_ENVIRONMENT",
    "DOTNET_APPLICATIONNAME",
    "DOTNET_CONTENTROOT",
];

pub(crate) fn apply(command: &mut CommandBuilder) {
    command.env_clear();
    for (name, value) in std::env::vars_os() {
        if name.to_str().is_some_and(|key| !is_host_reserved(key)) {
            command.env(name, value);
        }
    }
}

fn is_host_reserved(name: &str) -> bool {
    RESERVED_NAMES
        .iter()
        .any(|reserved| name.eq_ignore_ascii_case(reserved))
        || RESERVED_PREFIXES.iter().any(|prefix| {
            name.get(..prefix.len())
                .is_some_and(|start| start.eq_ignore_ascii_case(prefix))
        })
}
