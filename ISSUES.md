# Known Issues

Below are problems we’re aware of but haven’t fixed yet.  
If you can help, comment on—or better, open a pull request for—the linked issue.

| ID  | Short Description								| Work-around							| Status										|
| #11 | Multiple netapp controllers cause confusion     | None									| **In Progress**								|
| #10 | Retention daily is not keeping correct retention| None									| **In Progress**								|
| #9  | Toggle volumes not getting filled				| None									| **Fixed in 1.0.2510.3115**					|
| #8  | Failed to apply Export-policy during restore	| Run again								| **Minor patch but not tested fully yet**		|
| #7  | vmid should not be added to vmid.conf			| remove entry							| **Fixed**										|
| #6  | paused VM not resumed by BareProx				| manual resume							| **Fixed by lchanouha**						|
| #5  | Snapmirror relationships not updating			| none									| **Fixed**										|
| #4  | EFI-drives										| Move them offline						| **Fixed**										|
| #3  | Cloud-Init Drives								| Recreate them according to Proxmox	| **No fix available**							|
| #2  | TPM-devices										| Do not use!							| **In progress by Proxmox**					|
| #1  | Restore from Secondary does not work.			| None									| **Fixed**										|