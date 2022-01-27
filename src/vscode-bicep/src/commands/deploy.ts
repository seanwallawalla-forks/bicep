// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
import vscode from "vscode";
import { ext } from '../extensionVariables';
import { Command } from "./types";
import { LanguageClient } from "vscode-languageclient/node";
import { IActionContext, parseError } from "vscode-azureextensionui";
import { AzureAccount } from "../azure-account.api";
import { window } from "vscode";
import { SubscriptionTreeItem } from '../tree/SubscriptionTreeItem';
import { ResourceManagementClient } from "@azure/arm-resources";
import { appendToOutputChannel } from "../utils/logger";

export class DeployCommand implements Command {
  public readonly id = "bicep.deploy";
  public constructor(private readonly client: LanguageClient) {}

  public async execute(
    _context: IActionContext,
    documentUri?: vscode.Uri | undefined
  ): Promise<void> {
    documentUri ??= vscode.window.activeTextEditor?.document.uri;

    if (!documentUri) {
      return;
    }

    if (documentUri.scheme === "output") {
      // The output panel in VS Code was implemented as a text editor by accident. Due to breaking change concerns,
      // it won't be fixed in VS Code, so we need to handle it on our side.
      // See https://github.com/microsoft/vscode/issues/58869#issuecomment-422322972 for details.
      vscode.window.showInformationMessage(
        "We are unable to get the Bicep file to build when the output panel is focused. Please focus a text editor first when running the command."
      );

      return;
    }

    try {
      const subscription = await ext.tree.showTreeItemPicker<SubscriptionTreeItem>(SubscriptionTreeItem.contextValue, _context);

      if (subscription) {
        const resourceGroupItems = loadResourceGroupItems(subscription.subscription.subscriptionId);
        const resourceGroup = await _context.ui.showQuickPick(resourceGroupItems, {
          placeHolder: "Please select resource group",
        });

        const deploymentName = await window.showInputBox({
          prompt: "Enter deployment name",
        });

        const subscriptionId = subscription.subscription.subscriptionId;
        const resourceGroupName = resourceGroup?.resourceGroup.id;

        if (subscriptionId) {
          const deployOutput: string = await this.client.sendRequest(
            "workspace/executeCommand",
            {
              command: "deploy",
              arguments: [
                documentUri.fsPath,
                subscriptionId,
                resourceGroupName,
                deploymentName,
              ],
            }
          );
          appendToOutputChannel(deployOutput);
        }
      }
    } catch (err) {
      this.client.error("Deploy failed", parseError(err).message, true);
    }
  }
}

async function loadResourceGroupItems(subscriptionId: string) {
  const azureAccount = vscode.extensions.getExtension<AzureAccount>(
    "ms-vscode.azure-account"
  )!.exports;
  const session = azureAccount.sessions[0];

  const resources = new ResourceManagementClient(
    session.credentials2,
    subscriptionId
  );
  const resourceGroups = await listAll(
    resources.resourceGroups,
    resources.resourceGroups.list()
  );
  resourceGroups.sort((a, b) => (a.name || "").localeCompare(b.name || ""));
  return resourceGroups.map((resourceGroup) => ({
    label: resourceGroup.name || "",
    description: resourceGroup.location,
    resourceGroup,
  }));
}

async function listAll<T>(
  client: { listNext(nextPageLink: string): Promise<PartialList<T>> },
  first: Promise<PartialList<T>>
): Promise<T[]> {
  const all: T[] = [];
  for (
    let list = await first;
    list.length || list.nextLink;
    list = list.nextLink ? await client.listNext(list.nextLink) : []
  ) {
    all.push(...list);
  }
  return all;
}

export interface PartialList<T> extends Array<T> {
  nextLink?: string;
}
